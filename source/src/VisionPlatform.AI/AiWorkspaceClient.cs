using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionPlatform.AI;

public sealed record AiServiceEvent(string Kind, string? TaskId, JsonElement Data);
public interface IAiWorkspaceClient : IAsyncDisposable
{
    bool IsRunning { get; }
    AiServiceSettings Settings { get; }
    event EventHandler<AiServiceEvent>? EventReceived;
    Task StartAsync(AiServiceSettings settings, CancellationToken cancellationToken = default);
    Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default);
}
public sealed class AiServiceException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }

/// <summary>Owns only its Python service process. Training workers have independent persisted identities.
/// stdout is a bounded versioned protocol, stderr is diagnostic text, images never travel as base64.</summary>
public sealed class AiWorkspaceClient : IAiWorkspaceClient
{
    public const int MaximumMessageCharacters = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private Process? _process;
    private CancellationTokenSource? _lifetime;
    private Task _reader = Task.CompletedTask, _diagnostics = Task.CompletedTask;
    private bool _disposed;
    public AiServiceSettings Settings { get; private set; } = new();
    public bool IsRunning { get { try { return _process is { HasExited: false }; } catch (InvalidOperationException) { return false; } } }
    public event EventHandler<AiServiceEvent>? EventReceived;
    public async Task StartAsync(AiServiceSettings settings, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning) { if (Settings != settings) throw new InvalidOperationException("请先断开 AI 服务，再切换数据目录或环境。"); return; }
            var errors = settings.Validate(); if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            Settings = settings;
            Directory.CreateDirectory(settings.DataHome); Directory.CreateDirectory(settings.TransferRoot);
            var start = new ProcessStartInfo(settings.PythonPath)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
              StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = settings.DataHome };
            foreach (var arg in new[] { "-B", "-u", settings.ServiceScript, "--yolo-root", settings.YoloRoot, "--data-home", settings.DataHome, "--transfer-root", settings.TransferRoot }) start.ArgumentList.Add(arg);
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONIOENCODING"] = "utf-8"; start.Environment["YOLO_STUDIO_HOME"] = settings.DataHome;
            start.Environment["YOLO_AUTOINSTALL"] = "false";
            _lifetime?.Dispose(); _lifetime = new CancellationTokenSource();
            _process?.Dispose(); _process = Process.Start(start) ?? throw new IOException("无法启动 AI 业务服务。");
            _process.StandardInput.AutoFlush = true;
            _reader = ReadResponsesAsync(_process, _lifetime.Token);
            _diagnostics = ReadDiagnosticsAsync(_process, _lifetime.Token);
            await CallAsync("hello", new { }, cancellationToken).ConfigureAwait(false);
        }
        catch { await StopProcessAsync().ConfigureAwait(false); throw; }
        finally { _lifecycle.Release(); }
    }
    public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning) throw new InvalidOperationException("AI 服务未连接。普通视觉流程仍可使用。");
        var id = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
        deadline.CancelAfter(Settings.RequestTimeoutMilliseconds);
        try
        {
            var line = JsonSerializer.Serialize(new { version = 1, requestId = id, method, payload = payload ?? new { } });
            if (line.Length > MaximumMessageCharacters) throw new ArgumentException("AI 请求超过4Mi字符限制，请分批操作。");
            await _writeGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { await _process!.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            return await pending.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime!.IsCancellationRequested)
        { throw new TimeoutException("AI 服务请求超时；未自动重试。请查看任务状态，避免重复导入或启动训练。"); }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task ReadResponsesAsync(Process process, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            var reader = new BoundedLineReader(process.StandardOutput, MaximumMessageCharacters);
            while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }); var root = document.RootElement;
                if (root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("AI 服务协议版本不受支持。");
                if (root.TryGetProperty("event", out var kind))
                {
                    Emit(new(kind.GetString() ?? "", root.TryGetProperty("taskId", out var task) && task.ValueKind == JsonValueKind.String ? task.GetString() : null,
                        root.TryGetProperty("data", out var data) ? data.Clone() : JsonSerializer.SerializeToElement(new { })));
                    continue;
                }
                var id = root.GetProperty("requestId").GetString()!;
                if (!_pending.TryGetValue(id, out var pending)) continue;
                if (root.GetProperty("success").GetBoolean()) pending.TrySetResult(root.GetProperty("result").Clone());
                else
                {
                    var error = root.GetProperty("error");
                    pending.TrySetException(new AiServiceException(error.GetProperty("code").GetString() ?? "service_error", error.GetProperty("message").GetString() ?? "AI 操作失败。"));
                }
            }
            if (!ct.IsCancellationRequested) failure = new IOException("AI 服务进程已退出。任务历史仍保留；请重新连接并查询状态。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(failure ?? new OperationCanceledException("AI 服务已断开。"));
            if (failure is not null)
            {
                // An invalid/exhausted protocol cannot be kept as an apparently healthy connection.
                // Kill only this service, preserving independently tracked training workers.
                try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { }
                Emit(new("error", null, JsonSerializer.SerializeToElement(new { message = failure.Message })));
            }
        }
    }
    private async Task ReadDiagnosticsAsync(Process process, CancellationToken ct)
    {
        try
        {
            var reader = new BoundedLineReader(process.StandardError, 65536);
            while (await reader.ReadAsync(ct).ConfigureAwait(false) is { } line)
                Emit(new("diagnostic", null, JsonSerializer.SerializeToElement(new { message = line[..Math.Min(line.Length, 4096)] })));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
    }
    private void Emit(AiServiceEvent message)
    { try { EventReceived?.Invoke(this, message); } catch { /* Presentation failures cannot stop protocol draining. */ } }
    private async Task StopProcessAsync()
    {
        _lifetime?.Cancel();
        var process = _process; _process = null;
        if (process is not null)
        {
            try { if (!process.HasExited) { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } }
            catch { try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch { } }
        }
        try { await Task.WhenAll(_reader, _diagnostics).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        process?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (IsRunning)
                try { await CallAsync("shutdown", new { stop_training = false, detach_training = true }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            _disposed = true; await StopProcessAsync().ConfigureAwait(false); _lifetime?.Dispose();
        }
        finally { _lifecycle.Release(); }
    }
    private sealed class BoundedLineReader(StreamReader reader, int maximum)
    {
        private readonly char[] _buffer = new char[4096]; private int _offset, _count;
        public async Task<string?> ReadAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_offset == _count) { _count = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false); _offset = 0; if (_count == 0) return line.Length == 0 ? null : line.ToString(); }
                var end = Array.IndexOf(_buffer, '\n', _offset, _count - _offset); var length = (end < 0 ? _count : end) - _offset;
                if (line.Length + length > maximum) throw new InvalidDataException("AI 服务消息超过大小上限。");
                line.Append(_buffer, _offset, length); _offset += length;
                if (end >= 0) { _offset++; return line.ToString().TrimEnd('\r'); }
            }
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisionPlatform.Algorithms.AI;

/// <summary>Session-owned private pipe. Capacity=2 incl active; no frame drops or implicit request retry.</summary>
public sealed class ResidentYoloBridge : IYoloBridgeRunner, IAsyncDisposable
{
    public const string ServiceKey = "visionstudio.yolo.resident.v2";
    private readonly SemaphoreSlim _slots = new(2, 2), _serial = new(1, 1);
    private readonly SemaphoreSlim _frames = new(2, 2);
    private readonly CancellationTokenSource _closing = new();
    private Process? _process, _worker; private WindowsInferenceJob? _job; private Task? _stderr;
    private string _identity = "", _epoch = ""; private int _queued, _disposed, _admitted, _peakQueued; private long _restarts;
    public int QueueDepth => Volatile.Read(ref _queued);
    public int AdmittedFrames => Volatile.Read(ref _admitted);
    public int PeakQueueDepth => Volatile.Read(ref _peakQueued);
    public int? ProcessId { get { try { return _worker is { HasExited: false } p ? p.Id : null; } catch (InvalidOperationException) { return null; } } }
    public long RestartCount => Interlocked.Read(ref _restarts);
    public string Epoch => _epoch;
    public async Task<IDisposable> AcquireFrameBudgetAsync(YoloQueuePolicy policy, int timeoutMs, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        if (!await _frames.WaitAsync(policy == YoloQueuePolicy.Reject ? 0 : timeoutMs, linked.Token).ConfigureAwait(false))
        { if (policy == YoloQueuePolicy.Reject) throw new InvalidOperationException("Resident detection queue full (capacity2); frame rejected explicitly, no latest-frame replacement."); throw new TimeoutException("Resident frame admission timed out before encoding."); }
        if (Volatile.Read(ref _disposed) != 0) { _frames.Release(); throw new ObjectDisposedException(nameof(ResidentYoloBridge)); }
        Interlocked.Increment(ref _admitted);
        return new BudgetLease(this);
    }
    private sealed class BudgetLease(ResidentYoloBridge owner) : IDisposable
    { private ResidentYoloBridge? _owner = owner; public void Dispose() { var service=Interlocked.Exchange(ref _owner,null); if(service is not null){Interlocked.Decrement(ref service._admitted);service._frames.Release();} } }
    public async Task<string> RunAsync(string pythonExecutable, string bridgeScript, string requestPath, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = new CancellationTokenSource(timeoutMilliseconds); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _closing.Token);
        var watch = Stopwatch.StartNew();
        try { await _slots.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_closing.IsCancellationRequested)
        { throw new TimeoutException("Resident queue timeout before admission; no request sent."); }
        var depth=Interlocked.Increment(ref _queued);
        int peak; do { peak=Volatile.Read(ref _peakQueued); } while(depth>peak && Interlocked.CompareExchange(ref _peakQueued,depth,peak)!=peak);
        var serial = false;
        try
        {
            await _serial.WaitAsync(linked.Token).ConfigureAwait(false); serial = true; var queuedMs = watch.Elapsed.TotalMilliseconds;
            var request = JsonNode.Parse(await File.ReadAllTextAsync(requestPath, linked.Token).ConfigureAwait(false))!.AsObject();
            var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(requestPath)))!;
            var start = Stopwatch.StartNew(); await StartAsync(pythonExecutable, bridgeScript, root, request["yolo_root"]?.GetValue<string>() ?? "transport-fixture", linked.Token).ConfigureAwait(false); var startupMs = start.Elapsed.TotalMilliseconds;
            var id = request["request_id"]!.GetValue<string>();
            var response = await ExchangeAsync(new { protocol = 2, epoch = _epoch, request_id = id, operation = "infer", request }, linked.Token).ConfigureAwait(false);
            if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException("Resident inference: " + error.GetString());
            var payload = response.GetProperty("result");
            if (payload.GetProperty("request_id").GetString()!=id ||
                payload.GetProperty("image_frame_id").GetString()!=request["image_frame_id"]!.GetValue<string>() ||
                payload.GetProperty("run_id").GetString()!=request["run_id"]!.GetValue<string>() ||
                !string.Equals(payload.GetProperty("model_sha256").GetString(),request["expected_sha256"]!.GetValue<string>(),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Resident result request/run/frame/model identity mismatch; channel invalidated.");
            var result = JsonNode.Parse(response.GetProperty("result").GetRawText())!.AsObject(); var timing = result["timings"]!.AsObject();
            timing["queue_wait_ms"] = queuedMs; timing["service_start_handshake_ms"] = startupMs;
            timing["service_pid"] = ProcessId; timing["model_loads"] = response.GetProperty("model_loads").GetInt32();
            timing["cached_models"] = response.GetProperty("cached_models").GetInt32(); timing["restarts"] = RestartCount;
            timing["queue_depth_at_response"] = QueueDepth; timing["peak_queue_depth"] = PeakQueueDepth; timing["admitted_frames"] = AdmittedFrames;
            return result.ToJsonString();
        }
        catch (Exception) when (serial)
        {
            await StopAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (timeout.IsCancellationRequested) throw new TimeoutException("Resident request/queue timeout; owned process terminated; no late result or automatic retry.");
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_closing.IsCancellationRequested)
        { throw new TimeoutException("Resident queue timeout before execution; no request sent."); }
        finally { if (serial) _serial.Release(); Interlocked.Decrement(ref _queued); _slots.Release(); }
    }
    private async Task StartAsync(string python, string bridge, string root, string componentRoot, CancellationToken ct)
    {
        var identity = Path.GetFullPath(python) + "|" + Path.GetFullPath(bridge) + "|" + root + "|" + componentRoot;
        if (_process is { HasExited: false } && ProcessId is not null && _identity == identity) return;
        await StopAsync().ConfigureAwait(false); _epoch = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = root };
        foreach (var arg in new[] { "-B", "-u", Path.Combine(Path.GetDirectoryName(bridge)!, "resident_worker.py"), "--root", root, "--epoch", _epoch }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8"; start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["YOLO_CONFIG_DIR"] = Path.Combine(root, "VisionStudio-YoloSettings-" + _epoch);
        // This settings directory is owned by this service and removed only after process exit.
        _process = Process.Start(start) ?? throw new IOException("Resident Python could not start.");
        _job = WindowsInferenceJob.Attach(_process); _stderr = DrainAsync(_process.StandardError, _process);
        var hello = await ReadAsync(_process.StandardOutput, ct).ConfigureAwait(false);
        if (hello.GetProperty("protocol").GetInt32() != 2 || hello.GetProperty("epoch").GetString() != _epoch || !hello.GetProperty("ready").GetBoolean()) throw new InvalidDataException("Resident handshake mismatch.");
        _worker = Process.GetProcessById(hello.GetProperty("pid").GetInt32());
        if (_job is not null) _job.ContainWorker(_process, _worker);
        _identity = identity; Interlocked.Increment(ref _restarts);
    }
    public async Task<JsonElement> HealthAsync(CancellationToken ct = default)
    {
        using var timeout = new CancellationTokenSource(5000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _closing.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { if (_process is not { HasExited: false }) throw new InvalidOperationException("Resident service is not running; health does not start it."); return await ExchangeAsync(new { protocol = 2, epoch = _epoch, request_id = Guid.NewGuid().ToString("N"), operation = "health" }, linked.Token); }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        finally { _serial.Release(); }
    }
    public async Task UnloadAsync(CancellationToken ct = default)
    {
        using var timeout = new CancellationTokenSource(5000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _closing.Token);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { if (_process is { HasExited: false }) await ExchangeAsync(new { protocol = 2, epoch = _epoch, request_id = Guid.NewGuid().ToString("N"), operation = "unload" }, linked.Token); }
        catch { await StopAsync().ConfigureAwait(false); throw; }
        finally { _serial.Release(); }
    }
    private async Task<JsonElement> ExchangeAsync(object command, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(command); if (Encoding.UTF8.GetByteCount(json) > 65535) throw new InvalidDataException("Resident request exceeds64 KiB.");
        var id = JsonSerializer.SerializeToElement(command).GetProperty("request_id").GetString();
        await _process!.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false); await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        var reply = await ReadAsync(_process.StandardOutput, ct).ConfigureAwait(false);
        if (reply.GetProperty("protocol").GetInt32() != 2 || reply.GetProperty("epoch").GetString() != _epoch || reply.GetProperty("request_id").GetString() != id) throw new InvalidDataException("Stale/wrong resident response rejected.");
        return reply;
    }
    private static async Task<JsonElement> ReadAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var ch = new char[1];
        while (await reader.ReadAsync(ch, ct).ConfigureAwait(false) != 0) { if (ch[0] == '\n') { using var json = JsonDocument.Parse(text.ToString()); return json.RootElement.Clone(); } text.Append(ch[0]); if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Resident response exceeds8 MiB."); }
        throw new IOException("Resident process disconnected before completing its response.");
    }
    private static async Task DrainAsync(StreamReader reader, Process process)
    {
        var buffer = new char[4096]; long count = 0; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0) { count += n; if (count > 1024 * 1024) { if (!process.HasExited) process.Kill(true); break; } }
    }
    private async Task StopAsync()
    {
        var p = _process; _process = null; _identity = "";
        if (p is null) return;
        var settings = p.StartInfo.Environment["YOLO_CONFIG_DIR"];
        try { _job?.Terminate(); if (!p.HasExited) p.Kill(true); await p.WaitForExitAsync().ConfigureAwait(false); if (_stderr is not null) await _stderr.ConfigureAwait(false); }
        finally { _job?.Dispose(); _job = null; _worker?.Dispose(); _worker = null; p.Dispose(); _stderr = null; if (settings is not null && Path.GetFileName(settings).StartsWith("VisionStudio-YoloSettings-", StringComparison.Ordinal) && Directory.Exists(settings)) Directory.Delete(settings, true); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _closing.Cancel(); await _serial.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); } finally { _serial.Release(); }
    }
}

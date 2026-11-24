using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>A vendor call that stalls can be terminated without abandoning a thread/native handle in the UI process.</summary>
public sealed class BoqWorkerSdkFactory(string sdkPath, string? workerPath = null) : IBoqSdkFactory
{
    public const int CommandTimeoutMilliseconds = 8000;
    private readonly string _sdkPath = Path.GetFullPath(sdkPath);
    private readonly string _workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "CameraAdapters", "Boq", "VisionPlatform.BoqWorker.exe"));
    public IReadOnlyList<BoqSdkIdentity> Enumerate()
    {
        using var worker = Open();
        return worker.Request("Enumerate").Deserialize<BoqSdkIdentity[]>() ?? [];
    }
    public IBoqSdkCamera Connect(string serial)
    {
        var worker = Open();
        try { worker.Request("Connect", JsonSerializer.SerializeToElement(serial)); return new WorkerCamera(worker); }
        catch { worker.Dispose(); throw; }
    }
    private WorkerConnection Open()
    {
        if (!File.Exists(_sdkPath)) throw new FileNotFoundException("缺少用户自带博峤SDK。请安装到CameraAdapters/Boq；离线算法和文件夹虚拟相机仍可使用。", _sdkPath);
        if (!File.Exists(_workerPath)) throw new FileNotFoundException("缺少独立博峤工作进程VisionPlatform.BoqWorker.exe。", _workerPath);
        return WorkerConnection.Start(_workerPath, _sdkPath);
    }

    private sealed class WorkerCamera : IBoqSdkCamera
    {
        private readonly WorkerConnection _worker;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _pump;
        private volatile bool _disposed;
        public event Action<BoqSdkFrame>? FrameReceived;
        public event Action? Disconnected;
        public WorkerCamera(WorkerConnection worker) { _worker = worker; _pump = Task.Run(ReadFramesAsync); }
        public IReadOnlyList<CameraParameterCapability> QueryCapabilities() => _worker.Request("Capabilities").Deserialize<CameraParameterCapability[]>() ?? [];
        public IReadOnlyDictionary<string, JsonElement> ReadParameters() => _worker.Request("ReadParameters").Deserialize<Dictionary<string, JsonElement>>() ?? [];
        public void SetParameter(string name, JsonElement value) => _worker.Request("SetParameter", JsonSerializer.SerializeToElement(new { Name = name, Value = value }));
        public void GrabOne(uint timeoutMilliseconds) => _worker.Request("GrabOne", JsonSerializer.SerializeToElement(timeoutMilliseconds));
        public void Start() => _worker.Request("Start");
        public void Stop() => _worker.Request("Stop");
        private async Task ReadFramesAsync()
        {
            try
            {
                var header = new byte[28];
                while (true)
                {
                    await _worker.Frames.ReadExactlyAsync(header, _cancel.Token).ConfigureAwait(false);
                    if (BitConverter.ToInt32(header, 0) != BoqWorkerEntryPoint.FrameMagic) throw new InvalidDataException("博峤图像管道标识错误。");
                    var width = BitConverter.ToInt32(header, 4); var height = BitConverter.ToInt32(header, 8);
                    var size = BitConverter.ToInt32(header, 12); var format = BitConverter.ToInt32(header, 16);
                    var ticks = BitConverter.ToInt64(header, 20);
                    if (width == 0 && height == 0 && size == 0) throw new IOException("博峤SDK回调报告设备断线。");
                    if (width <= 0 || height <= 0 || (long)width * height > 100_000_000 || size <= 0 || size > BoqCameraDevice.MaximumFrameBytes)
                        throw new InvalidDataException("博峤工作进程返回超限图像。");
                    var pixels = new byte[size];
                    await _worker.Frames.ReadExactlyAsync(pixels, _cancel.Token).ConfigureAwait(false);
                    FrameReceived?.Invoke(new(pixels, width, height, size, format, new DateTimeOffset(ticks, TimeSpan.Zero)));
                }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch
            {
                if (!_disposed)
                {
                    try { Disconnected?.Invoke(); } catch { }
                    _worker.Abort();
                }
            }
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _cancel.Cancel(); _worker.Dispose();
            try { _pump.GetAwaiter().GetResult(); } catch { }
            _cancel.Dispose();
        }
    }

    private sealed class WorkerConnection : IDisposable
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _control;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly object _gate = new();
        private bool _disposed;
        public NamedPipeServerStream Frames { get; }
        private WorkerConnection(Process process, NamedPipeServerStream control, NamedPipeServerStream frames)
        { _process = process; _control = control; Frames = frames; _reader = new(control, Encoding.UTF8, false, 4096, true); _writer = new(control, new UTF8Encoding(false), 4096, true) { AutoFlush = true }; }
        public static WorkerConnection Start(string executable, string sdk)
        {
            var key = "VisionStudio.Boq." + Guid.NewGuid().ToString("N");
            var control = new NamedPipeServerStream(key + ".control", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var frames = new NamedPipeServerStream(key + ".frames", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Process? process = null;
            try
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "--control", key + ".control", "--frames", key + ".frames", "--sdk", sdk }) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new IOException("无法启动博峤工作进程。");
                // Drain vendor console output; it must never mix with the framed control/image protocols.
                process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                using var deadline = new CancellationTokenSource(CommandTimeoutMilliseconds);
                Task.WhenAll(control.WaitForConnectionAsync(deadline.Token), frames.WaitForConnectionAsync(deadline.Token)).GetAwaiter().GetResult();
                return new(process, control, frames);
            }
            catch
            {
                if (process is not null) { try { if (!process.HasExited) process.Kill(true); } catch { } process.Dispose(); }
                control.Dispose(); frames.Dispose(); throw;
            }
        }
        public JsonElement Request(string operation, JsonElement? value = null)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    using var deadline = new CancellationTokenSource(CommandTimeoutMilliseconds);
                    var command = JsonSerializer.Serialize(new BoqWorkerCommand(operation, value));
                    _writer.WriteLineAsync(command.AsMemory(), deadline.Token).GetAwaiter().GetResult();
                    var line = _reader.ReadLineAsync(deadline.Token).GetAwaiter().GetResult();
                    if (line is null || line.Length > 256 * 1024) throw new InvalidDataException("博峤控制响应缺失或超限。");
                    var response = JsonSerializer.Deserialize<BoqWorkerResponse>(line) ?? throw new InvalidDataException("博峤控制响应无效。");
                    if (!response.Success) throw new IOException(response.Error ?? "博峤SDK命令失败。");
                    return response.Value ?? JsonSerializer.SerializeToElement<object?>(null);
                }
                catch (OperationCanceledException)
                { Abort(); throw new TimeoutException($"博峤 {operation} 超过{CommandTimeoutMilliseconds / 1000}秒，已终止专用工作进程；请检查设备/SDK并重新打开。"); }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                { Abort(); throw; }
            }
        }
        public void Abort()
        {
            try { if (!_process.HasExited) _process.Kill(true); } catch { }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try { if (!_process.HasExited) Request("Close"); } catch { }
                _disposed = true; Abort(); _reader.Dispose(); _writer.Dispose(); _control.Dispose(); Frames.Dispose(); _process.Dispose();
            }
        }
    }
}

internal sealed record BoqWorkerCommand(string Operation, JsonElement? Value);
internal sealed record BoqWorkerResponse(bool Success, JsonElement? Value = null, string? Error = null);

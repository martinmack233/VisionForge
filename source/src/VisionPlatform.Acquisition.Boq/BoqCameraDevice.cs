using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>Callbacks copy pixels before returning and publish to one bounded slot. Algorithms never run on SDK threads.</summary>
public sealed class BoqCameraDevice(CameraDeviceInfo info, IBoqSdkFactory factory) : ICameraDevice
{
    public const long MaximumFrameBytes = 384L * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _callbackGate = new();
    private IBoqSdkCamera? _camera;
    private sealed record FramePacket(ImageFrame Frame, CameraFrameInfo Info);
    private Channel<FramePacket>? _frames;
    private TaskCompletionSource<ImageFrame>? _single;
    private CancellationTokenSource? _stream;
    private int _state = (int)CameraState.Closed, _consumer;
    private long _sequence, _dropped;
    private CameraFrameInfo? _lastFrame;
    private string? _lastError;
    public CameraDeviceInfo Info { get; } = info;
    public CameraState State => (CameraState)Volatile.Read(ref _state);
    public CameraFrameInfo? LastFrame => Volatile.Read(ref _lastFrame);
    public string? LastError => Volatile.Read(ref _lastError);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public CameraCapabilities Capabilities { get; private set; } = new(true, true, Enum.GetValues<PixelFormat>(), []);
    public event EventHandler? DeviceDisconnected;

    public async Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckDisposed();
            if (State is CameraState.Open or CameraState.Acquiring) return;
            var camera = await Task.Run(() => factory.Connect(Info.DeviceId), cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                camera.FrameReceived += OnFrame;
                camera.Disconnected += OnDisconnected;
                var parameters = await Task.Run(camera.QueryCapabilities, cancellationToken).ConfigureAwait(false);
                _camera = camera;
                Capabilities = new(true, true, Enum.GetValues<PixelFormat>(), parameters);
                _lastError = null; Volatile.Write(ref _state, (int)CameraState.Open);
            }
            catch { camera.FrameReceived -= OnFrame; camera.Disconnected -= OnDisconnected; camera.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ObjectDisposedException)
        { _lastError = ex.GetBaseException().Message; Volatile.Write(ref _state, (int)CameraState.Faulted); throw; }
        finally { _gate.Release(); }
    }

    public async Task<ImageFrame> CaptureSingleAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TaskCompletionSource<ImageFrame>? capture = null;
        var transferred = false;
        try
        {
            CheckOpen(stopped: true);
            capture = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_callbackGate) _single = capture;
            // SDK timeout applies to its capture request; the independent watchdog bounds callback waiting too.
            await Task.Run(() => _camera!.GrabOne(3000), cancellationToken).WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var frame = await capture.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transferred = true;
            return frame;
        }
        catch (OperationCanceledException)
        {
            // The SDK exposes no request identifier/cancel-grab API. Require reopening after cancellation
            // rather than allowing a late callback to satisfy a later single-capture request.
            if (capture is not null) { _lastError = "单帧采集已取消；请重新打开设备以排除迟到帧。"; Volatile.Write(ref _state, (int)CameraState.Faulted); }
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _lastError = ex.GetBaseException().Message; if (capture is not null) Volatile.Write(ref _state, (int)CameraState.Faulted); throw; }
        finally
        {
            lock (_callbackGate)
            {
                if (ReferenceEquals(_single, capture)) _single = null;
                // Cancellation may win concurrently with an arriving callback. Release its unpublished owned frame.
                if (!transferred && capture?.Task.IsCompletedSuccessfully == true) capture.Task.Result.Dispose();
            }
            _gate.Release();
        }
    }

    public async Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckOpen(stopped: true);
            if (Volatile.Read(ref _consumer) != 0) throw new InvalidOperationException("请先释放上一次采集读取器。");
            _stream?.Dispose(); _stream = new();
            _frames = Channel.CreateBounded<FramePacket>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
            Volatile.Write(ref _state, (int)CameraState.Acquiring);
            try { await Task.Run(() => _camera!.Start(), cancellationToken).ConfigureAwait(false); }
            catch { Volatile.Write(ref _state, (int)CameraState.Open); Drain(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<ImageFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _consumer, 1, 0) != 0) throw new InvalidOperationException("博峤采集流仅支持一个读取者。");
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("请先启动采集。");
            var frames = _frames ?? throw new InvalidOperationException("请先启动采集。");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.Token);
            while (await frames.Reader.WaitToReadAsync(cancel.Token).ConfigureAwait(false))
            {
                if (!frames.Reader.TryRead(out var packet)) continue;
                if (cancel.IsCancellationRequested) { packet.Frame.Dispose(); cancel.Token.ThrowIfCancellationRequested(); }
                Volatile.Write(ref _lastFrame, packet.Info);
                yield return packet.Frame;
            }
        }
        finally { Interlocked.Exchange(ref _consumer, 0); }
    }

    public async Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        _stream?.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_camera is not null && _stream is not null)
            {
                await Task.Run(() => _camera?.Stop(), cancellationToken).ConfigureAwait(false);
                if (State == CameraState.Acquiring) Volatile.Write(ref _state, (int)CameraState.Open);
            }
            lock (_callbackGate) Drain();
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        _stream?.Cancel();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == CameraState.Disposed) return;
            var camera = _camera; _camera = null;
            lock (_callbackGate)
            {
                Volatile.Write(ref _state, (int)CameraState.Closed);
                _single?.TrySetException(new IOException("相机已关闭。")); _single = null; Drain();
            }
            if (camera is not null)
            {
                camera.FrameReceived -= OnFrame; camera.Disconnected -= OnDisconnected;
                await Task.Run(camera.Dispose, cancellationToken).ConfigureAwait(false);
            }
            _stream?.Dispose(); _stream = null; _lastFrame = null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadParametersAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CheckOpen(); return await Task.Run(() => _camera!.ReadParameters(), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SetParameterAsync(string name, JsonElement value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckOpen();
            var capability = Capabilities.Parameters.SingleOrDefault(p => p.Name == name)
                ?? throw new NotSupportedException("当前设备未提供参数：" + name);
            if (!capability.Writable) throw new InvalidOperationException("参数只读：" + name);
            if (capability.RequiresStopped && State == CameraState.Acquiring) throw new InvalidOperationException("停止采集后才能修改：" + name);
            await Task.Run(() => _camera!.SetParameter(name, value), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (State == CameraState.Disposed) return;
        try { await CloseAsync().ConfigureAwait(false); }
        finally { Volatile.Write(ref _state, (int)CameraState.Disposed); }
    }

    private void OnFrame(BoqSdkFrame source)
    {
        lock (_callbackGate)
        {
            if (State != CameraState.Acquiring && _single is null) return;
            ImageFrame? frame = null;
            try
            {
                frame = CopyFrame(source);
                var sequence = Interlocked.Increment(ref _sequence);
                var frameInfo = new CameraFrameInfo(frame.FrameId, sequence, frame.Timestamp, frame.Width, frame.Height, frame.Stride, frame.PixelFormat, Info.DeviceId);
                if (_single is { } single)
                {
                    _single = null;
                    Volatile.Write(ref _lastFrame, frameInfo);
                    if (single.TrySetResult(frame)) frame = null;
                    return;
                }
                if (State != CameraState.Acquiring || _frames is not { } frames) return;
                while (!frames.Writer.TryWrite(new(frame, frameInfo)))
                {
                    if (!frames.Reader.TryRead(out var dropped)) return;
                    dropped.Frame.Dispose(); Interlocked.Increment(ref _dropped);
                }
                frame = null;
            }
            catch (Exception ex)
            {
                _lastError = ex.GetBaseException().Message;
                _single?.TrySetException(ex); _single = null;
                Volatile.Write(ref _state, (int)CameraState.Faulted);
                _frames?.Writer.TryComplete(ex); _stream?.Cancel();
            }
            finally { frame?.Dispose(); }
        }
    }

    public static ImageFrame CopyFrame(BoqSdkFrame source)
    {
        var format = source.PixelFormat switch
        {
            0x01080001 => PixelFormat.Mono8,
            0x01080009 => PixelFormat.BayerRG8,
            0x0108000A => PixelFormat.BayerGB8,
            0x02180014 => PixelFormat.RGB8,
            0x02180015 => PixelFormat.BGR8,
            _ => throw new NotSupportedException($"博峤像素格式 0x{source.PixelFormat:X8} 不在已支持的五种8位格式内。")
        };
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height > 100_000_000)
            throw new InvalidDataException("博峤图像尺寸无效或超过100MP限制。");
        if (source.ImageSize <= 0 || source.ImageSize > MaximumFrameBytes || source.Buffer.Length < source.ImageSize || source.ImageSize % source.Height != 0)
            throw new InvalidDataException("SDK未提供stride，imageSize必须能明确描述逐行数据；当前缓冲长度或排列不明确。");
        var stride = source.ImageSize / source.Height;
        var rowBytes = checked(source.Width * format.BytesPerPixel());
        if (stride < rowBytes) throw new InvalidDataException("博峤SDK缓冲小于声明的图像像素行。");
        using var builder = new ImageFrameBuilder(source.Width, source.Height, format, stride);
        for (var row = 0; row < source.Height; row++)
            source.Buffer.AsSpan(checked(row * stride), rowBytes).CopyTo(builder.GetRowSpan(row));
        return builder.Build(timestamp: source.ReceivedAt);
    }

    private void OnDisconnected()
    {
        lock (_callbackGate)
        {
            if (State is CameraState.Closed or CameraState.Disposed) return;
            _lastError = "博峤SDK报告设备断线；请停止并重新打开设备。";
            Volatile.Write(ref _state, (int)CameraState.Faulted);
            _single?.TrySetException(new IOException(_lastError)); _single = null;
            _stream?.Cancel(); _frames?.Writer.TryComplete(new IOException(_lastError)); Drain();
        }
        // Notification does not touch WPF or execute a graph. Subscribers must dispatch their own UI work.
        try { DeviceDisconnected?.Invoke(this, EventArgs.Empty); } catch { /* Never unwind into native SDK code. */ }
    }
    private void Drain()
    {
        _frames?.Writer.TryComplete();
        if (_frames is { } frames) while (frames.Reader.TryRead(out var frame)) frame.Frame.Dispose();
        _frames = null;
    }
    private void CheckDisposed() => ObjectDisposedException.ThrowIf(State == CameraState.Disposed, this);
    private void CheckOpen(bool stopped = false)
    {
        CheckDisposed();
        if (_camera is null || State is not (CameraState.Open or CameraState.Acquiring)) throw new InvalidOperationException("请先打开博峤相机。");
        if (stopped && State != CameraState.Open) throw new InvalidOperationException("请先停止连续预览/检测。");
    }
}

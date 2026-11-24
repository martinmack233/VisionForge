using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Acquisition;

/// <summary>Deterministic pull-based file acquisition. There is no queued producer and at most one in-flight decode.</summary>
public sealed class VirtualCameraDevice : ICameraDevice
{
    public const int MaximumFiles = 10000;
    public const long MaximumPixels = 100_000_000;
    public const long MaximumEncodedBytes = 512L * 1024 * 1024;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff", ".pgm" };
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _disposeGate = new();
    private CancellationTokenSource? _stream;
    private Task? _disposeTask;
    private string[] _files = [];
    private CameraOpenOptions? _options;
    private CameraFrameInfo? _lastFrame;
    private string? _lastError;
    private int _state = (int)CameraState.Closed, _consumer, _index;
    private long _sequence;
    public CameraDeviceInfo Info { get; }
    public CameraState State => (CameraState)Volatile.Read(ref _state);
    public CameraFrameInfo? LastFrame => Volatile.Read(ref _lastFrame);
    public string? LastError => Volatile.Read(ref _lastError);
    public CameraOpenOptions? CurrentOptions => Volatile.Read(ref _options);
    public CameraCapabilities Capabilities { get; } = new(true, true,
        Array.AsReadOnly(Enum.GetValues<PixelFormat>()), Array.AsReadOnly(new CameraParameterCapability[]
        {
            new("FolderPath", "图像文件夹", CameraParameterKind.String, true, true),
            new("FrameRate", "目标帧率", CameraParameterKind.Number, true, true, .1, 1000),
            new("Loop", "循环播放", CameraParameterKind.Boolean, true, true),
            new("PixelFormat", "文件像素解释", CameraParameterKind.Enumeration, true, true, AllowedValues: Array.AsReadOnly(new[] { "Auto", "Mono8", "BayerRG8", "BayerGB8", "RGB8", "BGR8" })),
            new("Width", "当前图像宽度", CameraParameterKind.Integer, true, false, 0),
            new("Height", "当前图像高度", CameraParameterKind.Integer, true, false, 0),
            new("Stride", "当前图像步长", CameraParameterKind.Integer, true, false, 0),
            new("Sequence", "已采集帧序号", CameraParameterKind.Integer, true, false, 0),
            new("FileCount", "文件数", CameraParameterKind.Integer, true, false, 0, MaximumFiles),
            new("NextIndex", "下一文件索引", CameraParameterKind.Integer, true, false, 0, MaximumFiles)
        }));

    public VirtualCameraDevice(CameraDeviceInfo? info = null) => Info = info ?? new("folder", VirtualCameraProvider.Id, "文件夹虚拟相机", true);
    public static IReadOnlyList<string> ValidateOptions(CameraOpenOptions options, bool allowEmptyFolder = false)
    {
        ArgumentNullException.ThrowIfNull(options); var errors = new List<string>();
        if (options.FolderPath is null || options.FolderPath.Length > 4096 || options.FolderPath.IndexOf('\0') >= 0 || !allowEmptyFolder && string.IsNullOrWhiteSpace(options.FolderPath))
            errors.Add("FolderPath must name an image directory (at most 4096 characters).");
        if (!double.IsFinite(options.FrameRate) || options.FrameRate < .1 || options.FrameRate > 1000) errors.Add("FrameRate must be finite in [0.1,1000].");
        if (options.PixelFormat is { } format && !Enum.IsDefined(format)) errors.Add("Unsupported camera PixelFormat.");
        return errors;
    }
    public static CameraOpenOptions NormalizeOptions(CameraOpenOptions options)
    {
        var errors = ValidateOptions(options); if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        return options with { FolderPath = Path.GetFullPath(options.FolderPath) };
    }
    public async Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken = default)
    {
        options = NormalizeOptions(options);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckNotDisposed();
            if (State is CameraState.Open or CameraState.Acquiring)
            {
                if (_options == options) return;
                throw new InvalidOperationException("Close the camera or stop and edit a supported parameter before changing its configuration.");
            }
            var files = await ReadFileListAsync(options.FolderPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceConfiguration(options, files); SetState(CameraState.Open);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is not ObjectDisposedException)
        { _lastError = e.Message; if (State != CameraState.Acquiring && State != CameraState.Open) SetState(CameraState.Faulted); throw; }
        finally { _operation.Release(); }
    }
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        CancelStream();
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == CameraState.Disposed) return;
            _files = []; _options = null; _index = 0; _sequence = 0; _lastFrame = null; _lastError = null;
            SetState(CameraState.Closed);
        }
        finally { _operation.Release(); }
    }
    public Task<ImageFrame> CaptureSingleAsync(CancellationToken cancellationToken = default) => CaptureCoreAsync(false, cancellationToken);
    private async Task<ImageFrame> CaptureCoreAsync(bool continuous, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false); ImageFrame? loaded = null;
        try
        {
            CheckNotDisposed(); cancellationToken.ThrowIfCancellationRequested();
            var expected = continuous ? CameraState.Acquiring : CameraState.Open;
            if (State != expected) throw new InvalidOperationException(continuous
                ? "Camera acquisition is not running."
                : State == CameraState.Acquiring ? "Stop continuous camera acquisition before running a single capture or an algorithm graph." : "Open the camera before capturing a frame.");
            var options = _options ?? throw new InvalidOperationException("Camera has no active configuration.");
            var next = _index;
            if (next >= _files.Length)
            {
                if (!options.Loop) { _lastError = "End of virtual-camera sequence; Loop is disabled."; throw new EndOfStreamException(_lastError); }
                next = 0;
            }
            var path = _files[next];
            // Native decoding is synchronous. Run it away from UI and check cancellation before publishing its owned reference.
            loaded = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("Virtual-camera image disappeared after opening the directory.", path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Virtual-camera images must not be symbolic links.");
                if (file.Length > MaximumEncodedBytes) throw new InvalidDataException("Virtual-camera image exceeds the 512 MiB encoded-file budget.");
                return ImageFile.Load(path, options.PixelFormat);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if ((long)loaded.Width * loaded.Height > MaximumPixels) throw new InvalidDataException("Decoded virtual-camera image exceeds the 100 MP frame budget.");
            _index = next + 1; var sequence = checked(++_sequence);
            _lastFrame = new(loaded.FrameId, sequence, loaded.Timestamp, loaded.Width, loaded.Height, loaded.Stride, loaded.PixelFormat, Path.GetFileName(path));
            _lastError = null; var result = loaded; loaded = null; return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (EndOfStreamException) { throw; }
        catch (Exception e) when (e is not ObjectDisposedException)
        {
            _lastError = e.Message;
            // Invalid operation requests do not fault a healthy device; an actual IO/format/decode failure does.
            if (e is not InvalidOperationException && State is CameraState.Open or CameraState.Acquiring) { SetState(CameraState.Faulted); CancelStream(); }
            throw;
        }
        finally { loaded?.Dispose(); _operation.Release(); }
    }
    public async Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckNotDisposed();
            if (State == CameraState.Acquiring) return;
            if (State != CameraState.Open) throw new InvalidOperationException("Open the camera before starting acquisition.");
            if (Volatile.Read(ref _consumer) != 0) throw new InvalidOperationException("Dispose the previous camera stream enumerator before starting another one.");
            _stream?.Dispose(); _stream = new(); _lastError = null; SetState(CameraState.Acquiring);
        }
        finally { _operation.Release(); }
    }
    public async IAsyncEnumerable<ImageFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _consumer, 1, 0) != 0) throw new InvalidOperationException("A camera stream supports one consumer only.");
        CancellationTokenSource? cancel = null;
        try
        {
            await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                CheckNotDisposed();
                if (State != CameraState.Acquiring || _stream is null) throw new InvalidOperationException("Start camera acquisition before reading frames.");
                cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stream.Token);
            }
            finally { _operation.Release(); }
            while (true)
            {
                cancel.Token.ThrowIfCancellationRequested(); var began = Stopwatch.GetTimestamp(); ImageFrame? frame;
                try { frame = await CaptureCoreAsync(true, cancel.Token).ConfigureAwait(false); }
                catch (EndOfStreamException) { yield break; }
                // Ownership transfers only when MoveNext succeeds. Cancellation before yield releases the pending frame.
                if (cancel.IsCancellationRequested) { frame.Dispose(); cancel.Token.ThrowIfCancellationRequested(); }
                yield return frame;
                var remaining = TimeSpan.FromSeconds(1 / (_options?.FrameRate ?? 5)) - Stopwatch.GetElapsedTime(began);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancel.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            cancel?.Dispose();
            await _operation.WaitAsync().ConfigureAwait(false);
            try { if (State == CameraState.Acquiring) SetState(CameraState.Open); }
            finally { Interlocked.Exchange(ref _consumer, 0); _operation.Release(); }
        }
    }
    public async Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        CancelStream(); await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (State == CameraState.Acquiring) SetState(CameraState.Open); }
        finally { _operation.Release(); }
    }
    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadParametersAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckNotDisposed(); var p = _options;
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["FolderPath"] = J(p?.FolderPath ?? ""), ["FrameRate"] = J(p?.FrameRate ?? 5), ["Loop"] = J(p?.Loop ?? true), ["PixelFormat"] = J(p?.PixelFormat?.ToString() ?? "Auto"),
                ["Width"] = J(_lastFrame?.Width ?? 0), ["Height"] = J(_lastFrame?.Height ?? 0), ["Stride"] = J(_lastFrame?.Stride ?? 0),
                ["Sequence"] = J(_sequence), ["FileCount"] = J(_files.Length), ["NextIndex"] = J(_index)
            };
        }
        finally { _operation.Release(); }
    }
    public async Task SetParameterAsync(string name, JsonElement value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckNotDisposed(); if (State != CameraState.Open) throw new InvalidOperationException("Camera parameters can be changed only while the camera is open and stopped.");
            var options = _options!;
            options = name switch
            {
                "FolderPath" when value.ValueKind == JsonValueKind.String => options with { FolderPath = value.GetString()! },
                "FrameRate" when value.ValueKind == JsonValueKind.Number => options with { FrameRate = value.GetDouble() },
                "Loop" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => options with { Loop = value.GetBoolean() },
                "PixelFormat" when value.ValueKind == JsonValueKind.String => options with { PixelFormat = ParseFormat(value.GetString()!) },
                _ => throw new ArgumentException("Unknown, read-only or incorrectly typed camera parameter: " + name)
            };
            options = NormalizeOptions(options);
            if (!string.Equals(options.FolderPath, _options!.FolderPath, StringComparison.Ordinal))
            {
                var files = await ReadFileListAsync(options.FolderPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested(); ReplaceConfiguration(options, files);
            }
            else { _options = options; _lastError = null; }
        }
        finally { _operation.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { _disposeTask ??= DisposeCoreAsync(); return new(_disposeTask); }
    }
    private async Task DisposeCoreAsync()
    {
        CancelStream(); await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == CameraState.Disposed) return;
            _files = []; _options = null; _lastFrame = null; _lastError = null;
            _stream?.Dispose(); _stream = null; SetState(CameraState.Disposed);
        }
        finally { _operation.Release(); }
    }
    private static async Task<string[]> ReadFileListAsync(string folder, CancellationToken ct) => await Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Virtual-camera folder does not exist: " + folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Virtual-camera folders must not be symbolic links.");
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            ct.ThrowIfCancellationRequested(); if (!Extensions.Contains(Path.GetExtension(file))) continue;
            files.Add(Path.GetFullPath(file)); if (files.Count > MaximumFiles) throw new InvalidDataException("Virtual-camera directory exceeds the 10000-image budget.");
        }
        if (files.Count == 0) throw new InvalidDataException("Virtual-camera folder contains no supported images.");
        return files.Order(StringComparer.Ordinal).ToArray();
    }, ct).ConfigureAwait(false);
    private void ReplaceConfiguration(CameraOpenOptions options, string[] files)
    { _options = options; _files = files; _index = 0; _sequence = 0; _lastFrame = null; _lastError = null; }
    private static PixelFormat? ParseFormat(string value) => value == "Auto" ? null : Enum.TryParse<PixelFormat>(value, false, out var format) && Enum.IsDefined(format) && Enum.GetName(format) == value ? format : throw new ArgumentException("Unknown camera pixel format.");
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private void CheckNotDisposed() => ObjectDisposedException.ThrowIf(State == CameraState.Disposed, this);
    private void SetState(CameraState state) => Volatile.Write(ref _state, (int)state);
    private void CancelStream()
    {
        var stream = Volatile.Read(ref _stream); if (stream is null) return;
        try { stream.Cancel(); } catch (ObjectDisposedException) { }
    }
}

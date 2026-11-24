using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Acquisition;

/// <summary>Owned by the existing Runtime execution service scope. It never owns references returned to frame consumers.</summary>
public sealed class CameraSession : IAsyncDisposable
{
    private sealed record Entry(string Provider, string Device, CameraOpenOptions Options, ICameraDevice Camera);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _devices = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private volatile bool _disposed;
    public CameraProviderRegistry Providers { get; }
    public CameraSession(CameraProviderRegistry? providers = null, CancellationToken lifetimeCancellation = default)
    { Providers = providers ?? new(); _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation); }
    public static CameraSession For(IExecutionServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetOrCreate("visionplatform.acquisition.camera-session.v1", () => new CameraSession(CameraProviderRegistry.CreateInstalled(), services.LifetimeCancellation));
    }
    public async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        return await Providers.EnumerateAsync(cancel.Token).ConfigureAwait(false);
    }
    public Task<ICameraDevice> GetVirtualDeviceAsync(string cameraId, CameraOpenOptions options, CancellationToken cancellationToken = default)
        => GetDeviceAsync(cameraId, VirtualCameraProvider.Id, "folder", options, cancellationToken);
    public async Task<ICameraDevice> GetDeviceAsync(string cameraId, string providerId, string deviceId,
        CameraOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId); ArgumentNullException.ThrowIfNull(options);
        if (cameraId.Length > 128) throw new ArgumentException("CameraId exceeds 128 characters.", nameof(cameraId));
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (providerId == VirtualCameraProvider.Id) options = VirtualCameraDevice.NormalizeOptions(options);
            if (_devices.TryGetValue(cameraId, out var entry))
            {
                if (entry.Provider == providerId && entry.Device == deviceId && SameOptions(entry.Camera is VirtualCameraDevice virtualDevice ? virtualDevice.CurrentOptions ?? entry.Options : entry.Options, options) && entry.Camera.State is CameraState.Open or CameraState.Acquiring)
                    return entry.Camera;
                if (entry.Camera.State == CameraState.Acquiring) throw new InvalidOperationException("Stop camera acquisition before changing its configuration.");
                _devices.Remove(cameraId); await entry.Camera.DisposeAsync().ConfigureAwait(false);
            }
            var device = Providers.Create(providerId, deviceId);
            try
            {
                await device.OpenAsync(options, cancel.Token).ConfigureAwait(false);
                if (options.Parameters is not null)
                    foreach (var (name, value) in options.Parameters)
                    {
                        var capability = device.Capabilities.Parameters.FirstOrDefault(p => p.Name == name);
                        if (capability is not { Readable: true, Writable: true })
                            throw new ArgumentException("Only readable, writable configuration parameters may be restored; explicit device actions cannot be replayed: " + name);
                        await device.SetParameterAsync(name, value, cancel.Token).ConfigureAwait(false);
                    }
                cancel.Token.ThrowIfCancellationRequested();
            }
            catch { await device.DisposeAsync().ConfigureAwait(false); throw; }
            _devices.Add(cameraId, new(providerId, deviceId, options, device)); return device;
        }
        finally { _gate.Release(); }
    }
    private static bool SameOptions(CameraOpenOptions a, CameraOpenOptions b)
    {
        if (a.FolderPath != b.FolderPath || a.FrameRate != b.FrameRate || a.Loop != b.Loop || a.PixelFormat != b.PixelFormat) return false;
        if ((a.Parameters?.Count ?? 0) != (b.Parameters?.Count ?? 0)) return false;
        return a.Parameters is null || a.Parameters.All(p => b.Parameters is not null && b.Parameters.TryGetValue(p.Key, out var other) && p.Value.GetRawText() == other.GetRawText());
    }
    public async Task CloseDeviceAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { if (_devices.Remove(cameraId, out var entry)) await entry.Camera.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { _disposeTask ??= CloseAllAsync(); return new(_disposeTask); }
    }
    private async Task CloseAllAsync()
    {
        _disposed = true; _lifetime.Cancel(); await _gate.WaitAsync().ConfigureAwait(false);
        var errors = new List<Exception>();
        try
        {
            foreach (var entry in _devices.Values.Reverse())
                try { await entry.Camera.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { errors.Add(e); }
            _devices.Clear();
        }
        finally { _gate.Release(); _lifetime.Dispose(); }
        if (errors.Count > 0) throw new AggregateException("Camera session cleanup failed.", errors);
    }
}

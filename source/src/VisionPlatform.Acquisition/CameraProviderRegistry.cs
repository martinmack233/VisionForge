using System.Reflection;
using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition;

/// <summary>Explicit provider registration; trusted optional adapter assemblies may be discovered by the host.</summary>
public sealed class CameraProviderRegistry
{
    /// <summary>Only the known, application-installed adapter is considered. Its SDK is loaded lazily.</summary>
    public static CameraProviderRegistry CreateInstalled()
    {
        var registry = new CameraProviderRegistry();
        var adapter = Path.Combine(AppContext.BaseDirectory, "VisionPlatform.Acquisition.Boq.dll");
        if (File.Exists(adapter))
        {
            try { registry.Discover(Assembly.LoadFrom(adapter)); }
            catch (Exception ex) { registry.AdapterDiagnostic = "博峤适配器不可用：" + ex.GetBaseException().Message; }
        }
        return registry;
    }
    public string? AdapterDiagnostic { get; private set; }
    public IReadOnlyDictionary<string, string> Diagnostics
    {
        get
        {
            lock (_gate) return _providers.Values.OfType<ICameraProviderDiagnostics>()
                .Where(p => !string.IsNullOrEmpty(p.LastError)).ToDictionary(p => ((ICameraProvider)p).ProviderId, p => p.LastError!, StringComparer.Ordinal);
        }
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, ICameraProvider> _providers = new(StringComparer.Ordinal);
    public CameraProviderRegistry(bool registerVirtual = true)
    { if (registerVirtual) Register(new VirtualCameraProvider()); }
    public IReadOnlyList<string> ProviderIds { get { lock (_gate) return _providers.Keys.Order(StringComparer.Ordinal).ToArray(); } }
    public void Register(ICameraProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderId);
        if (provider.ProviderId.Length > 128) throw new ArgumentException("Camera provider ID exceeds 128 characters.");
        lock (_gate)
            if (!_providers.TryAdd(provider.ProviderId, provider)) throw new ArgumentException("Camera provider is already registered: " + provider.ProviderId);
    }
    public int Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly); var count = 0;
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ICameraProvider).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null) continue;
            Register((ICameraProvider)Activator.CreateInstance(type)!); count++;
        }
        return count;
    }
    public ICameraDevice Create(string providerId, string deviceId)
    {
        ICameraProvider provider;
        lock (_gate) provider = _providers.TryGetValue(providerId, out var found) ? found : throw new KeyNotFoundException("Camera provider is not installed: " + providerId);
        var device = provider.Create(deviceId) ?? throw new InvalidOperationException("Camera provider returned no device.");
        if (device.Info.ProviderId != providerId || device.Info.DeviceId != deviceId)
            throw new InvalidOperationException("Camera device identity differs from the selected provider/device.");
        return device;
    }
    public async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        ICameraProvider[] providers; lock (_gate) providers = _providers.Values.ToArray();
        var result = new List<CameraDeviceInfo>(); var identities = new HashSet<(string, string)>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var device in await provider.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (device.ProviderId != provider.ProviderId || string.IsNullOrWhiteSpace(device.DeviceId) || !identities.Add((device.ProviderId, device.DeviceId)))
                    throw new InvalidOperationException("Camera provider returned an invalid or duplicate device identity: " + provider.ProviderId);
                result.Add(device);
            }
        }
        return result.AsReadOnly();
    }
}

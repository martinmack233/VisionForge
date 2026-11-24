using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>Optional, lazy adapter. Missing/invalid vendor SDK never prevents offline startup.</summary>
public sealed class BoqCameraProvider : ICameraProvider, ICameraProviderDiagnostics
{
    public const string Id = "boq";
    private readonly IBoqSdkFactory _factory;
    private readonly Dictionary<string, BoqSdkIdentity> _identities = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _enumeration = new(1, 1);
    public string ProviderId => Id;
    public string SdkPath { get; }
    public string? LastError { get; private set; }
    public bool IsSdkAvailable => File.Exists(SdkPath);
    public BoqCameraProvider()
    {
        var machine = new VisionPlatform.Contracts.Deployment.MachineConfigurationStore().Load();
        SdkPath = string.IsNullOrWhiteSpace(machine.BoqSdkPath) ? Path.Combine(AppContext.BaseDirectory, "CameraAdapters", "Boq", "BoqApi_CSharp.dll") : machine.BoqSdkPath;
        _factory = new BoqWorkerSdkFactory(SdkPath, string.IsNullOrWhiteSpace(machine.BoqWorkerPath) ? null : machine.BoqWorkerPath);
    }
    public BoqCameraProvider(string sdkPath) : this(new BoqWorkerSdkFactory(sdkPath), Path.GetFullPath(sdkPath)) { }
    public BoqCameraProvider(string sdkPath, string workerPath) : this(new BoqWorkerSdkFactory(sdkPath, workerPath), Path.GetFullPath(sdkPath)) { }
    public BoqCameraProvider(IBoqSdkFactory factory, string sdkPath = "")
    { _factory = factory ?? throw new ArgumentNullException(nameof(factory)); SdkPath = sdkPath; }

    public async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        await _enumeration.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identities = await Task.Run(_factory.Enumerate, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_identities)
            {
                _identities.Clear();
                foreach (var identity in identities.Take(128))
                {
                    ValidateSerial(identity.Serial);
                    if (!_identities.TryAdd(identity.Serial, identity)) throw new InvalidDataException("博峤SDK返回重复设备序列号。");
                }
                LastError = null;
                return _identities.Values.Select(ToInfo).ToArray();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = "博峤相机不可用：" + ex.GetBaseException().Message;
            lock (_identities) _identities.Clear();
            // Enumeration of optional hardware must not suppress the independently registered virtual provider.
            return [];
        }
        finally { _enumeration.Release(); }
    }

    public ICameraDevice Create(string deviceId)
    {
        ValidateSerial(deviceId);
        lock (_identities)
        {
            var info = _identities.TryGetValue(deviceId, out var identity) ? ToInfo(identity) : new(deviceId, Id, "博峤相机 · " + deviceId, false);
            return new BoqCameraDevice(info, _factory);
        }
    }
    private static CameraDeviceInfo ToInfo(BoqSdkIdentity identity) => new(identity.Serial, Id,
        $"{(identity.IsSimulator ? "博峤 SDK 模拟设备" : "博峤相机")} · {identity.Model} · {identity.Serial}", identity.IsSimulator);
    private static void ValidateSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length > 128 || serial.Any(char.IsControl))
            throw new ArgumentException("博峤设备身份必须是1–128字符的序列号。");
    }
}

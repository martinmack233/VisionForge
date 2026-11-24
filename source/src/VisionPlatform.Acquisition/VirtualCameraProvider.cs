using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition;

/// <summary>File-backed devices only. No vendor SDK or persistent background producer.</summary>
public sealed class VirtualCameraProvider : ICameraProvider
{
    public const string Id = "virtual-folder";
    public string ProviderId => Id;
    public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CameraDeviceInfo>>([new("folder", Id, "文件夹虚拟相机", true)]);
    }
    public ICameraDevice Create(string deviceId)
    {
        if (deviceId != "folder") throw new ArgumentException("Unknown virtual camera device. Enumerate devices before creating one.", nameof(deviceId));
        return new VirtualCameraDevice(new(deviceId, Id, "文件夹虚拟相机", true));
    }
}

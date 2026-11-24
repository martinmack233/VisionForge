using System.Text.Json;
using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>Borrowed SDK bytes are valid only until the callback returns.</summary>
public sealed record BoqSdkFrame(byte[] Buffer, int Width, int Height, int ImageSize, int PixelFormat, DateTimeOffset ReceivedAt);
public sealed record BoqSdkIdentity(string Serial, string Model, string Manufacturer, string UserName)
{
    public bool IsSimulator => Model.Contains("Fake", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Replaceable transport seam. No SDK object escapes into the platform contracts.</summary>
public interface IBoqSdkCamera : IDisposable
{
    event Action<BoqSdkFrame>? FrameReceived;
    event Action? Disconnected;
    IReadOnlyList<CameraParameterCapability> QueryCapabilities();
    IReadOnlyDictionary<string, JsonElement> ReadParameters();
    void SetParameter(string name, JsonElement value);
    void GrabOne(uint timeoutMilliseconds);
    void Start();
    void Stop();
}

public interface IBoqSdkFactory
{
    IReadOnlyList<BoqSdkIdentity> Enumerate();
    IBoqSdkCamera Connect(string serial);
}

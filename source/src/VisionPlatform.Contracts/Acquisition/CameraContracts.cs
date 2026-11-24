using System.Text.Json;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Contracts.Acquisition;

public enum CameraState { Closed, Open, Acquiring, Faulted, Disposed }
public enum CameraParameterKind { Integer, Number, Boolean, String, Enumeration }
public sealed record CameraDeviceInfo(string DeviceId, string ProviderId, string DisplayName, bool IsVirtual);
public sealed record CameraParameterCapability(string Name, string DisplayName, CameraParameterKind Kind,
    bool Readable, bool Writable, double? Minimum = null, double? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null, bool RequiresStopped = true)
{
    public string? Unit { get; init; }
    public double? Step { get; init; }
}
public sealed record CameraCapabilities(bool SupportsSingleCapture, bool SupportsContinuousCapture,
    IReadOnlyList<PixelFormat> PixelFormats, IReadOnlyList<CameraParameterCapability> Parameters);
/// <summary>Only parameters applicable to a provider should be used. No hardware SDK types cross this boundary.</summary>
public sealed record CameraOpenOptions
{
    public string FolderPath { get; init; } = "";
    public double FrameRate { get; init; } = 5;
    public bool Loop { get; init; } = true;
    public PixelFormat? PixelFormat { get; init; }
    [System.ComponentModel.Browsable(false)] public IReadOnlyDictionary<string, JsonElement>? Parameters { get; init; }
}
public sealed record CameraFrameInfo(Guid FrameId, long Sequence, DateTimeOffset Timestamp,
    int Width, int Height, int Stride, PixelFormat PixelFormat, string SourceName);

/// <summary>Capture and every yielded frame transfer one owned ImageFrame reference to the caller.
/// A provider never releases references held by its consumers. Continuous streams are single-consumer.</summary>
public interface ICameraDevice : IAsyncDisposable
{
    CameraDeviceInfo Info { get; }
    CameraState State { get; }
    CameraCapabilities Capabilities { get; }
    CameraFrameInfo? LastFrame { get; }
    string? LastError { get; }
    Task OpenAsync(CameraOpenOptions options, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task<ImageFrame> CaptureSingleAsync(CancellationToken cancellationToken = default);
    Task StartAcquisitionAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ImageFrame> ReadFramesAsync(CancellationToken cancellationToken = default);
    Task StopAcquisitionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, JsonElement>> ReadParametersAsync(CancellationToken cancellationToken = default);
    Task SetParameterAsync(string name, JsonElement value, CancellationToken cancellationToken = default);
}

public interface ICameraProvider
{
    string ProviderId { get; }
    Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);
    ICameraDevice Create(string deviceId);
}

/// <summary>Optional provider diagnostics, independent of any desktop UI.</summary>
public interface ICameraProviderDiagnostics
{
    string? LastError { get; }
}

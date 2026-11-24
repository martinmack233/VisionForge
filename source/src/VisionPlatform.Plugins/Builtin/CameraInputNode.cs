using System.ComponentModel;
using VisionPlatform.Acquisition;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public sealed record CameraInputParameters
{
    [DisplayName("相机会话标识")] public string CameraId { get; init; } = "camera-main";
    [DisplayName("设备源")] public string ProviderId { get; init; } = "boq";
    [DisplayName("设备身份/序列号")] public string DeviceId { get; init; } = "";
    [DisplayName("设备配置（由设备页写入）")] public CameraOpenOptions Options { get; init; } = new();
}

/// <summary>Provider-neutral capture. SDK types, UI and handles never enter the project model.</summary>
public sealed class CameraInputNode : NodeBase<CameraInputParameters>
{
    public override NodeDescriptor Descriptor => Describe("CameraInput", [],
        [new("Image", typeof(ImageFrame)), new("FrameInfo", typeof(CameraFrameInfo))]);
    protected override IReadOnlyList<string> Validate(CameraInputParameters p)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(p.CameraId) || p.CameraId.Length > 128) errors.Add("CameraId requires 1..128 characters.");
        if (string.IsNullOrWhiteSpace(p.ProviderId)) errors.Add("Select a camera provider.");
        if (string.IsNullOrWhiteSpace(p.DeviceId)) errors.Add("Select a camera device identity.");
        if (p.Options is null) errors.Add("Camera options must not be null.");
        return errors;
    }
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<CameraInputParameters>();
        var errors = Validate(p); if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        var own = context.Services is null ? new CameraSession(CameraProviderRegistry.CreateInstalled()) : null;
        var cameras = own ?? CameraSession.For(context.Services!);
        ImageFrame? frame = null;
        try
        {
            var device = await cameras.GetDeviceAsync(p.CameraId, p.ProviderId, p.DeviceId, p.Options, cancellationToken).ConfigureAwait(false);
            frame = await device.CaptureSingleAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var info = device.LastFrame;
            if (info is null || info.FrameId != frame.FrameId)
                throw new InvalidOperationException("The acquired frame and device metadata do not identify the same frame; stop competing capture consumers.");
            var output = new NodeOutput(new Dictionary<string, object> { ["Image"] = frame, ["FrameInfo"] = info }, [frame]);
            frame = null; return output;
        }
        finally { frame?.Dispose(); if (own is not null) await own.DisposeAsync().ConfigureAwait(false); }
    }
}

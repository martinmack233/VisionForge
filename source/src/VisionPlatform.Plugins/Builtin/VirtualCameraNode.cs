using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Acquisition;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Builtin;

public sealed record VirtualCameraParameters
{
    [DisplayName("相机会话标识")] public string CameraId { get; init; } = "virtual-main";
    [DisplayName("图像文件夹")] public string FolderPath { get; init; } = "";
    [DisplayName("目标帧率"), Range(.1, 1000)] public double FrameRate { get; init; } = 5;
    [DisplayName("循环播放")] public bool Loop { get; init; } = true;
    [DisplayName("输入像素格式（关闭为自动）")] public PixelFormat? PixelFormat { get; init; }
}
public sealed class VirtualCameraNode : NodeBase<VirtualCameraParameters>
{
    public override NodeDescriptor Descriptor => Describe("VirtualCamera", [], [new("Image", typeof(ImageFrame)), new("FrameInfo", typeof(CameraFrameInfo))]);
    protected override IReadOnlyList<string> Validate(VirtualCameraParameters p)
    {
        var errors = VirtualCameraDevice.ValidateOptions(new() { FolderPath = p.FolderPath, FrameRate = p.FrameRate, Loop = p.Loop, PixelFormat = p.PixelFormat }, allowEmptyFolder: true).ToList();
        if (string.IsNullOrWhiteSpace(p.CameraId) || p.CameraId.Length > 128) errors.Add("CameraId must contain 1..128 characters.");
        return errors;
    }
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<VirtualCameraParameters>();
        var errors = Validate(p); if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        if (string.IsNullOrWhiteSpace(p.FolderPath)) throw new ArgumentException("Configure VirtualCamera FolderPath before executing acquisition.");
        cancellationToken.ThrowIfCancellationRequested();
        var options = new CameraOpenOptions { FolderPath = ProjectStore.ResolveResourcePath(context.ResourceRoot, p.FolderPath), FrameRate = p.FrameRate, Loop = p.Loop, PixelFormat = p.PixelFormat };
        var own = context.Services is null ? new CameraSession() : null;
        var cameras = own ?? CameraSession.For(context.Services!); ImageFrame? frame = null;
        try
        {
            var device = await cameras.GetVirtualDeviceAsync(p.CameraId, options, cancellationToken).ConfigureAwait(false);
            frame = await device.CaptureSingleAsync(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
            var info = device.LastFrame;
            if (info is null || info.FrameId != frame.FrameId) throw new InvalidOperationException("Camera frame metadata changed concurrently; stop other capture consumers before running the graph.");
            var output = new NodeOutput(new Dictionary<string, object> { ["Image"] = frame, ["FrameInfo"] = info }, [frame]); frame = null; return output;
        }
        finally { frame?.Dispose(); if (own is not null) await own.DisposeAsync().ConfigureAwait(false); }
    }
}

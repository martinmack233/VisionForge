using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;

namespace VisionPlatform.Plugins.Builtin;

public sealed record PixelFormatConvertParameters { public PixelFormat TargetFormat { get; init; } = PixelFormat.BGR8; }
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class PixelFormatConvertNode : NodeBase<PixelFormatConvertParameters>
{
    public override NodeDescriptor Descriptor => Describe("PixelFormatConvert",
        [new("Image", typeof(ImageFrame))], [new("ConvertedImage", typeof(ImageFrame)), new("AllocatedNewBuffer", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(PixelFormatConvertParameters p) =>
        Enum.IsDefined(p.TargetFormat) ? [] : ["Invalid target pixel format."];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var result = PixelFormatConverter.Convert(context.Input<ImageFrame>("Image"), context.Parameters<PixelFormatConvertParameters>().TargetFormat);
        var image = result.Image.Share();
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        { ["ConvertedImage"] = image, ["AllocatedNewBuffer"] = result.AllocatedNewBuffer }, [image]));
    }
}

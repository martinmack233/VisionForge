using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class RotateImageNode : NodeBase<RotateImageOptions>
{
    public override NodeDescriptor Descriptor => Describe("RotateImage", [new("Image", typeof(ImageFrame))],
        [new("RotatedImage", typeof(ImageFrame)), new("CoordinateTransform", typeof(CoordinateTransform2D))]);
    protected override IReadOnlyList<string> Validate(RotateImageOptions parameters) => RotateImageProcessor.Validate(parameters);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var result = RotateImageProcessor.Process(context.Input<ImageFrame>("Image"), context.Parameters<RotateImageOptions>(), cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["RotatedImage"] = result.Image, ["CoordinateTransform"] = result.CoordinateTransform }, [result.Image]));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class ResizeImageNode : NodeBase<ResizeImageOptions>
{
    public override NodeDescriptor Descriptor => Describe("ResizeImage", [new("Image", typeof(ImageFrame))],
        [new("ResizedImage", typeof(ImageFrame)), new("CoordinateTransform", typeof(CoordinateTransform2D))]);
    protected override IReadOnlyList<string> Validate(ResizeImageOptions parameters) => ResizeImageProcessor.Validate(parameters);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var result = ResizeImageProcessor.Process(context.Input<ImageFrame>("Image"), context.Parameters<ResizeImageOptions>(), cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ResizedImage"] = result.Image, ["CoordinateTransform"] = result.CoordinateTransform }, [result.Image]));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class FlipImageNode : NodeBase<FlipImageOptions>
{
    public override NodeDescriptor Descriptor => Describe("FlipImage", [new("Image", typeof(ImageFrame))],
        [new("FlippedImage", typeof(ImageFrame)), new("CoordinateTransform", typeof(CoordinateTransform2D))]);
    protected override IReadOnlyList<string> Validate(FlipImageOptions parameters) => FlipImageProcessor.Validate(parameters);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var result = FlipImageProcessor.Process(context.Input<ImageFrame>("Image"), context.Parameters<FlipImageOptions>(), cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["FlippedImage"] = result.Image, ["CoordinateTransform"] = result.CoordinateTransform }, [result.Image]));
    }
}

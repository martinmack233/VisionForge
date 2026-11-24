using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class CropImageNode : NodeBase<CropImageOptions>
{
    public override NodeDescriptor Descriptor => Describe("CropImage",
        [new("Image", typeof(ImageFrame)), new("CropRectangle", typeof(RectRoi), false)],
        [new("CroppedImage", typeof(ImageFrame)), new("CoordinateTransform", typeof(CoordinateTransform2D))]);
    protected override IReadOnlyList<string> Validate(CropImageOptions parameters) => CropImageProcessor.Validate(parameters);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var rectangle = context.TryInput<RectRoi>("CropRectangle", out var roi) ? roi : null;
        var result = CropImageProcessor.Process(context.Input<ImageFrame>("Image"), context.Parameters<CropImageOptions>(), rectangle, cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["CroppedImage"] = result.Image, ["CoordinateTransform"] = result.CoordinateTransform
        }, [result.Image]));
    }
}

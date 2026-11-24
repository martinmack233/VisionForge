using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class RotatedCropNode : NodeBase<RotatedCropOptions>
{
    public override NodeDescriptor Descriptor => Describe("RotatedCrop",
        [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false), new("Pose2D", typeof(Pose2D), false)],
        [new("CroppedImage", typeof(ImageFrame)), new("CoordinateTransform", typeof(CoordinateTransform2D))]);
    protected override IReadOnlyList<string> Validate(RotatedCropOptions parameters) => RotatedCropProcessor.Validate(parameters);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var roi = context.TryInput<RotatedRectRoi>("ROI", out var region) ? region : null;
        var pose = context.TryInput<Pose2D>("Pose2D", out var supplied) ? supplied : null;
        var result = RotatedCropProcessor.Process(context.Input<ImageFrame>("Image"), context.Parameters<RotatedCropOptions>(), roi, pose, cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["CroppedImage"] = result.Image, ["CoordinateTransform"] = result.CoordinateTransform
        }, [result.Image]));
    }
}

using VisionPlatform.Algorithms.ROI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public sealed record ROITransformParameters
{
    public Pose2D ReferencePose { get; init; } = new(0, 0);
    public RotatedRectRoi ReferenceROI { get; init; } = new(0, 0, 10, 10);
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class ROITransformNode : NodeBase<ROITransformParameters>
{
    public override NodeDescriptor Descriptor => Describe("ROITransform",
        [new("Pose2D", typeof(Pose2D)), new("Image", typeof(ImageFrame))], [new("ROI", typeof(RotatedRectRoi))]);
    protected override IReadOnlyList<string> Validate(ROITransformParameters p)
    {
        try { p.ReferenceROI.Validate(); return []; } catch (Exception ex) { return [ex.Message]; }
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<ROITransformParameters>();
        var roi = RoiTransformer.Transform(p.ReferenceROI, p.ReferencePose, context.Input<Pose2D>("Pose2D"), context.Input<ImageFrame>("Image"));
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ROI"] = roi }));
    }
}

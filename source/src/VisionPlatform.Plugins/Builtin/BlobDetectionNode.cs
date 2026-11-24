using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe("ROI")]
public sealed class BlobDetectionNode : NodeBase<BlobOptions>
{
    public override NodeDescriptor Descriptor => Describe("BlobDetection",
        [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("BlobResults", typeof(BlobResults)), new("BlobCount", typeof(int))]);
    protected override IReadOnlyList<string> Validate(BlobOptions p) =>
        p.MinArea <= 0 || p.MaxArea < p.MinArea ? ["Invalid Blob area range."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var roi = context.TryInput<RotatedRectRoi>("ROI", out var supplied) ? supplied : null;
        var blobs = BlobDetector.Detect(context.Input<ImageFrame>("Image"), roi, context.Parameters<BlobOptions>(), cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["BlobResults"] = blobs, ["BlobCount"] = blobs.Count }));
    }
}

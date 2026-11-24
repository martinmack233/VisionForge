using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class BlobFilterNode : NodeBase<BlobFilterOptions>
{
    public override NodeDescriptor Descriptor => Describe("BlobFilter", [new("BlobResults", typeof(BlobResults))],
        [new("FilteredBlobs", typeof(BlobResults)), new("BlobCount", typeof(int)), new("FilteredMask", typeof(ImageFrame)),
            new("BoundingBoxes", typeof(RectRoi[])), new("Centroids", typeof(Point2D[]))]);
    protected override IReadOnlyList<string> Validate(BlobFilterOptions p) => BlobFilter.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = BlobFilter.Apply(context.Input<BlobResults>("BlobResults"), context.Parameters<BlobFilterOptions>(), cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["FilteredBlobs"] = output.FilteredBlobs, ["BlobCount"] = output.BlobCount, ["FilteredMask"] = output.FilteredMask,
            ["BoundingBoxes"] = output.BoundingBoxes, ["Centroids"] = output.Centroids
        }, [output.FilteredMask]));
    }
}

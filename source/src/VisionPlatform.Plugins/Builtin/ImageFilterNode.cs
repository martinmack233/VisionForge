using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[SubflowSafe]
public sealed class ImageFilterNode : NodeBase<ImageFilterOptions>
{
    public override NodeDescriptor Descriptor => Describe("ImageFilter",
        [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("ProcessedImage", typeof(ImageFrame), false), new("NeighborhoodSum", typeof(FloatPlaneFrame), false)]);

    protected override IReadOnlyList<string> Validate(ImageFilterOptions p) => ImageFilterProcessor.Validate(p);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<ImageFilterOptions>();
        var input = context.Input<ImageFrame>("Image");
        context.TryInput<RotatedRectRoi>("ROI", out var roi);
        if (p.Method == ImageFilterMethod.BoxSum)
        {
            var sum = ImageFilterProcessor.Sum(input, p, roi, cancellationToken);
            return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["NeighborhoodSum"] = sum }, [sum]));
        }
        var output = ImageFilterProcessor.Process(input, p, roi, cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ProcessedImage"] = output }, [output]));
    }
}

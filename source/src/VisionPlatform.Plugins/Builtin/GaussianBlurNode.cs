using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public sealed record GaussianBlurParameters
{
    public GaussianBlurOptions Options { get; init; } = new();
    public RectRoi? ROI { get; init; }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class GaussianBlurNode : NodeBase<GaussianBlurParameters>
{
    private readonly IImageProcessingAlgorithm<GaussianBlurOptions> _algorithm = new GaussianBlurAlgorithm();
    public override NodeDescriptor Descriptor => Describe("GaussianBlur",
        [new("Image", typeof(ImageFrame)), new("ROI", typeof(RectRoi), false)], [new("ProcessedImage", typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(GaussianBlurParameters p) => _algorithm.Validate(p.Options);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<GaussianBlurParameters>();
        var roi = context.TryInput<RectRoi>("ROI", out var supplied) ? supplied : p.ROI;
        var output = _algorithm.Process(context.Input<ImageFrame>("Image"), p.Options, roi, cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ProcessedImage"] = output }, [output]));
    }
}

using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[SubflowSafe]
public sealed class ContourEnvelopeNode : NodeBase<ContourEnvelopeOptions>
{
    public override NodeDescriptor Descriptor => Describe("ContourEnvelope",
        [new("Collection", typeof(VisionCollection)), new("Mask", typeof(ImageFrame), false)],
        [new("Collection", typeof(VisionCollection)), new("Rectangles", typeof(VisionCollection)),
            new("Circles", typeof(VisionCollection)), new("Count", typeof(int)), new("ValidCount", typeof(int))]);

    protected override IReadOnlyList<string> Validate(ContourEnvelopeOptions options) => ContourEnvelopeProcessor.Validate(options);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        context.TryInput<ImageFrame>("Mask", out var mask);
        var results = ContourEnvelopeProcessor.Process(context.Input<VisionCollection>("Collection"),
            context.Parameters<ContourEnvelopeOptions>(), mask, cancellationToken);
        var items = results.Items.Cast<ContourEnvelopeItem>();
        var rectangles = new VisionCollection(VisionItemKind.Roi,
            items.Where(i => i.Rectangle is not null).Select(i => new RoiItem(i.Origin, i.Rectangle!) { OriginalItem = i }));
        var circles = new VisionCollection(VisionItemKind.Circle,
            items.Where(i => i.Circle is not null).Select(i => new CircleItem(i.Origin, i.Circle!)));
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["Collection"] = results, ["Rectangles"] = rectangles, ["Circles"] = circles,
            ["Count"] = results.Count, ["ValidCount"] = rectangles.Count + circles.Count
        }));
    }
}

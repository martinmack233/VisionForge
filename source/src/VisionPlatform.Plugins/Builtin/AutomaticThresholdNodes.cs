using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class OtsuThresholdNode : NodeBase<OtsuThresholdOptions>
{
    private readonly OtsuThresholdProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("OtsuThreshold", [new("Image", typeof(ImageFrame))],
        [new("BinaryImage", typeof(ImageFrame)), new("CalculatedThreshold", typeof(double))]);
    protected override IReadOnlyList<string> Validate(OtsuThresholdOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = _algorithm.ProcessWithThreshold(context.Input<ImageFrame>("Image"), context.Parameters<OtsuThresholdOptions>(), cancellationToken: cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["BinaryImage"] = output.BinaryImage, ["CalculatedThreshold"] = output.CalculatedThreshold }, [output.BinaryImage]));
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class AdaptiveThresholdNode : NodeBase<AdaptiveThresholdOptions>
{
    private readonly AdaptiveThresholdProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("AdaptiveThreshold", [new("Image", typeof(ImageFrame))], [new("BinaryImage", typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(AdaptiveThresholdOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = _algorithm.Process(context.Input<ImageFrame>("Image"), context.Parameters<AdaptiveThresholdOptions>(), cancellationToken: cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["BinaryImage"] = output }, [output]));
    }
}

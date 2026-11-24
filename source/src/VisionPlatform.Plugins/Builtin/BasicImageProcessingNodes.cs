using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class MedianBlurNode : NodeBase<MedianBlurOptions>
{
    private readonly MedianBlurProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("MedianBlur", [new("Image", typeof(ImageFrame))], [new("ProcessedImage", typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(MedianBlurOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = _algorithm.Process(context.Input<ImageFrame>("Image"), context.Parameters<MedianBlurOptions>(), cancellationToken: cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ProcessedImage"] = output }, [output]));
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class ThresholdNode : NodeBase<ThresholdOptions>
{
    private readonly ThresholdProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("Threshold", [new("Image", typeof(ImageFrame))],
        [new("ProcessedImage", typeof(ImageFrame)), new("ForegroundPixelCount", typeof(int))]);
    protected override IReadOnlyList<string> Validate(ThresholdOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = _algorithm.Process(context.Input<ImageFrame>("Image"), context.Parameters<ThresholdOptions>(), cancellationToken: cancellationToken);
        try
        {
            var count = ThresholdProcessor.CountForeground(output, cancellationToken);
            return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ProcessedImage"] = output, ["ForegroundPixelCount"] = count }, [output]));
        }
        catch { output.Dispose(); throw; }
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class MorphologyNode : NodeBase<MorphologyOptions>
{
    private readonly MorphologyProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("Morphology", [new("Image", typeof(ImageFrame))], [new("ProcessedImage", typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(MorphologyOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var output = _algorithm.Process(context.Input<ImageFrame>("Image"), context.Parameters<MorphologyOptions>(), cancellationToken: cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ProcessedImage"] = output }, [output]));
    }
}

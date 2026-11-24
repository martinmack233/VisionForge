using VisionPlatform.Contracts.Results;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class NormalizeNode : NodeBase<NormalizeOptions>
{
    private readonly NormalizeProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("Normalize", [new("Image", typeof(ImageFrame)), new("ROI", typeof(RectRoi), false)], [new("ProcessedImage", typeof(ImageFrame)),new("Statistics",typeof(EnhancementStatistics))]);
    protected override IReadOnlyList<string> Validate(NormalizeOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var image=context.Input<ImageFrame>("Image");
        var output = _algorithm.Process(image, context.Parameters<NormalizeOptions>(), context.TryInput<RectRoi>("ROI",out var roi)?roi:null,cancellationToken);
        return LegacyEnhancementOutput.Complete(image,output,"OpenCV common-channel min/max normalize; constant range maps to Minimum",cancellationToken);
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class EqualizeHistNode : NodeBase<EqualizeHistOptions>
{
    private readonly EqualizeHistProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("EqualizeHist", [new("Image", typeof(ImageFrame)), new("ROI", typeof(RectRoi), false)], [new("ProcessedImage", typeof(ImageFrame)),new("Statistics",typeof(EnhancementStatistics))]);
    protected override IReadOnlyList<string> Validate(EqualizeHistOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var roi = context.TryInput<RectRoi>("ROI", out var supplied) ? supplied : null;
        var image=context.Input<ImageFrame>("Image");var output = _algorithm.Process(image, context.Parameters<EqualizeHistOptions>(), roi, cancellationToken);
        return LegacyEnhancementOutput.Complete(image,output,"OpenCV Mono8 global histogram equalization inside the supplied ROI",cancellationToken);
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class ClaheNode : NodeBase<ClaheOptions>
{
    private readonly ClaheProcessor _algorithm = new();
    public override NodeDescriptor Descriptor => Describe("CLAHE", [new("Image", typeof(ImageFrame)),new("ROI",typeof(RectRoi),false)], [new("ProcessedImage", typeof(ImageFrame)),new("Statistics",typeof(EnhancementStatistics))]);
    protected override IReadOnlyList<string> Validate(ClaheOptions p) => _algorithm.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var image=context.Input<ImageFrame>("Image");var output = _algorithm.Process(image, context.Parameters<ClaheOptions>(),context.TryInput<RectRoi>("ROI",out var roi)?roi:null,cancellationToken);
        return LegacyEnhancementOutput.Complete(image,output,"OpenCV Mono8 CLAHE; tile grid is tile count; processing ROI defines the isolated interpolation domain",cancellationToken);
    }
}

internal static class LegacyEnhancementOutput
{
    public static ValueTask<NodeOutput> Complete(ImageFrame input,ImageFrame output,string rule,CancellationToken ct)
    {
        try
        {
            using var region=input.CreateView(checked((int)(output.CoordinateSpace.OriginX-input.CoordinateSpace.OriginX)),checked((int)(output.CoordinateSpace.OriginY-input.CoordinateSpace.OriginY)),output.Width,output.Height);
            var stats=EnhancementProcessors.Describe(region,output,rule,ct);
            return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"ProcessedImage",output},{"Statistics",stats}},[output]));
        }
        catch{output.Dispose();throw;}
    }
}

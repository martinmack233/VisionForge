using VisionPlatform.Contracts.Results;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public abstract class EnhancementNode<T>:NodeBase<T> where T:EnhancementBudgetOptions,new()
{
    protected abstract string TypeId{get;}
    public override NodeDescriptor Descriptor=>Describe(TypeId,[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],
        [new("ProcessedImage",typeof(ImageFrame)),new("Statistics",typeof(EnhancementStatistics))]);
    protected override IReadOnlyList<string> Validate(T p)=>EnhancementProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var r=EnhancementProcessors.Apply(c.Input<ImageFrame>("Image"),c.Parameters<T>(),c.TryInput<RotatedRectRoi>("ROI",out var roi)?roi:null,ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"ProcessedImage",r.Image},{"Statistics",r.Statistics}},[r.Image]));
    }
}
[SubflowSafe("ROI")]public sealed class LinearContrastNode:EnhancementNode<LinearContrastOptions>{protected override string TypeId=>"LinearContrast";}
[SubflowSafe("ROI")]public sealed class RangeStretchNode:EnhancementNode<RangeStretchOptions>{protected override string TypeId=>"RangeStretch";}
[SubflowSafe("ROI")]public sealed class PercentileStretchNode:EnhancementNode<PercentileStretchOptions>{protected override string TypeId=>"PercentileStretch";}
[SubflowSafe("ROI")]public sealed class GammaCorrectionNode:EnhancementNode<GammaCorrectionOptions>{protected override string TypeId=>"GammaCorrection";}
[SubflowSafe("ROI")]public sealed class InvertNode:EnhancementNode<EnhancementBudgetOptions>{protected override string TypeId=>"Invert";}

public abstract class DerivativeNode<T>:NodeBase<T> where T:DerivativeOptions,new()
{
    protected abstract string TypeId{get;}
    public override NodeDescriptor Descriptor=>Describe(TypeId,[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],
        [new("Derivative",typeof(FloatPlaneFrame)),new("Preview",typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(T p)=>EdgeProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var r=EdgeProcessors.Derivative(c.Input<ImageFrame>("Image"),c.Parameters<T>(),c.TryInput<RotatedRectRoi>("ROI",out var roi)?roi:null,ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"Derivative",r.Derivative},{"Preview",r.Preview}},[r.Derivative,r.Preview]));
    }
}
[SubflowSafe("ROI")]public sealed class SobelNode:DerivativeNode<SobelOptions>{protected override string TypeId=>"Sobel";}
[SubflowSafe("ROI")]public sealed class ScharrNode:DerivativeNode<ScharrOptions>{protected override string TypeId=>"Scharr";}
[SubflowSafe("ROI")]public sealed class LaplacianNode:DerivativeNode<LaplacianOptions>{protected override string TypeId=>"Laplacian";}
[SubflowSafe]public sealed class GradientMagnitudeDirectionNode:NodeBase<GradientMagnitudeDirectionOptions>
{
    public override NodeDescriptor Descriptor=>Describe("GradientMagnitudeDirection",[new("GradientX",typeof(FloatPlaneFrame)),new("GradientY",typeof(FloatPlaneFrame))],
        [new("Magnitude",typeof(FloatPlaneFrame)),new("Direction",typeof(FloatPlaneFrame)),new("Preview",typeof(ImageFrame)),new("DirectionValidMask",typeof(ImageFrame)),new("ZeroMagnitudeCount",typeof(int))]);
    protected override IReadOnlyList<string> Validate(GradientMagnitudeDirectionOptions p)=>Enum.IsDefined(p.AngleUnit)?[]:["方向单位无效。"];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var r=EdgeProcessors.MagnitudeDirection(c.Input<FloatPlaneFrame>("GradientX"),c.Input<FloatPlaneFrame>("GradientY"),c.Parameters<GradientMagnitudeDirectionOptions>(),ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"Magnitude",r.Magnitude},{"Direction",r.Direction},{"Preview",r.Preview},{"DirectionValidMask",r.DirectionValidMask},{"ZeroMagnitudeCount",r.ZeroMagnitudeCount}},[r.Magnitude,r.Direction,r.Preview,r.DirectionValidMask]));
    }
}

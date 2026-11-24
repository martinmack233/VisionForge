using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class CaliperNode : NodeBase<CaliperOptions>
{
    public override NodeDescriptor Descriptor => Describe("Caliper", [new("Image", typeof(ImageFrame))],
        [new("Points", typeof(EdgePointSet)), new("Result", typeof(MeasurementResult)), new("Valid", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(CaliperOptions p) => CaliperProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var output = CaliperProcessor.Process(c.Input<ImageFrame>("Image"), c.Parameters<CaliperOptions>(), ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Points"] = output.Points, ["Result"] = output.Result, ["Valid"] = output.Result.Valid }));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class CircleEdgePointsNode : NodeBase<CircleEdgePointsOptions>
{
    public override NodeDescriptor Descriptor => Describe("CircleEdgePoints", [new("Image", typeof(ImageFrame))],
        [new("Points", typeof(EdgePointSet)), new("Result", typeof(MeasurementResult)), new("Valid", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(CircleEdgePointsOptions p) => CircleEdgePointsProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var output = CircleEdgePointsProcessor.Process(c.Input<ImageFrame>("Image"), c.Parameters<CircleEdgePointsOptions>(), ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Points"] = output.Points, ["Result"] = output.Result, ["Valid"] = output.Result.Valid }));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class LineFitNode : NodeBase<LineFitOptions>
{
    public override NodeDescriptor Descriptor => Describe("LineFit", [new("Points", typeof(EdgePointSet))],
        [new("Result", typeof(MeasurementResult)), new("Line", typeof(LineGeometry), false), new("Angle", typeof(double)), new("Error", typeof(double)), new("PointCount", typeof(int)), new("Valid", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(LineFitOptions p) => LineFitProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var output = LineFitProcessor.Process(c.Input<EdgePointSet>("Points"), c.Parameters<LineFitOptions>(), ct);
        var values = new Dictionary<string, object> { ["Result"] = output.Result, ["Valid"] = output.Result.Valid,
            ["Angle"] = output.Result.Values.GetValueOrDefault("Angle"), ["Error"] = output.Result.Values.GetValueOrDefault("RmsError"), ["PointCount"] = (int)output.Result.Values["PointCount"] };
        if (output.Line is not null) values["Line"] = output.Line;
        return ValueTask.FromResult(new NodeOutput(values));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class CircleFitNode : NodeBase<CircleFitOptions>
{
    public override NodeDescriptor Descriptor => Describe("CircleFit", [new("Points", typeof(EdgePointSet))],
        [new("Result", typeof(MeasurementResult)), new("Circle", typeof(CircleGeometry), false), new("Center", typeof(Point2D), false),
         new("Radius", typeof(double)), new("Diameter", typeof(double)), new("Error", typeof(double)), new("Valid", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(CircleFitOptions p) => CircleFitProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var output = CircleFitProcessor.Process(c.Input<EdgePointSet>("Points"), c.Parameters<CircleFitOptions>(), ct);
        var values = new Dictionary<string, object> { ["Result"] = output.Result, ["Valid"] = output.Result.Valid,
            ["Radius"] = output.Result.Values.GetValueOrDefault("Radius"), ["Diameter"] = output.Result.Values.GetValueOrDefault("Diameter"), ["Error"] = output.Result.Values.GetValueOrDefault("RmsError") };
        if (output.Circle is not null) { values["Circle"] = output.Circle; values["Center"] = output.Circle.Center; }
        return ValueTask.FromResult(new NodeOutput(values));
    }
}

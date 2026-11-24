using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class DistanceMeasureNode : NodeBase<DistanceMeasureOptions>
{
    public override NodeDescriptor Descriptor => Describe("DistanceMeasure", [new("PointA", typeof(Point2D), false), new("PointB", typeof(Point2D), false), new("LineA", typeof(LineGeometry), false), new("LineB", typeof(LineGeometry), false)],
        [new("Result", typeof(MeasurementResult)), new("Valid", typeof(bool)), new("Distance", typeof(double)), new("ErrorReason", typeof(string))]);
    protected override IReadOnlyList<string> Validate(DistanceMeasureOptions p) => DistanceMeasureProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var result = DistanceMeasureProcessor.Process(c.Parameters<DistanceMeasureOptions>(), c.TryInput<Point2D>("PointA", out var a) ? a : null,
            c.TryInput<Point2D>("PointB", out var b) ? b : null, c.TryInput<LineGeometry>("LineA", out var la) ? la : null, c.TryInput<LineGeometry>("LineB", out var lb) ? lb : null, ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = result, ["Valid"] = result.Valid, ["Distance"] = result.Values.GetValueOrDefault("Distance"), ["ErrorReason"] = result.ErrorReason ?? "" }));
    }
}
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class AngleMeasureNode : NodeBase<AngleMeasureOptions>
{
    public override NodeDescriptor Descriptor => Describe("AngleMeasure", [new("LineA", typeof(LineGeometry), false), new("LineB", typeof(LineGeometry), false)],
        [new("Result", typeof(MeasurementResult)), new("Valid", typeof(bool)), new("Angle", typeof(double)), new("ErrorReason", typeof(string))]);
    protected override IReadOnlyList<string> Validate(AngleMeasureOptions p) => AngleMeasureProcessor.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var result = AngleMeasureProcessor.Process(c.Parameters<AngleMeasureOptions>(), c.TryInput<LineGeometry>("LineA", out var a) ? a : null, c.TryInput<LineGeometry>("LineB", out var b) ? b : null, ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = result, ["Valid"] = result.Valid, ["Angle"] = result.Values.GetValueOrDefault("Angle"), ["ErrorReason"] = result.ErrorReason ?? "" }));
    }
}

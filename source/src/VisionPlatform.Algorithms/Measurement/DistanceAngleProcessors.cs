using System.Collections.ObjectModel;
using System.ComponentModel;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;

namespace VisionPlatform.Algorithms.Measurement;

public enum DistanceMeasureMode { PointToPoint, PointToLine, ParallelLines }
public sealed record DistanceMeasureOptions
{
    [DisplayName("距离类型")] public DistanceMeasureMode Mode { get; init; }
    [DisplayName("点 A（未连端口时）")] public Point2D PointA { get; init; } = new(0, 0);
    [DisplayName("点 B（未连端口时）")] public Point2D PointB { get; init; } = new(10, 0);
    [DisplayName("直线 A（未连端口时）")] public LineGeometry LineA { get; init; } = new(new(0, 0), new(1, 0));
    [DisplayName("直线 B（未连端口时）")] public LineGeometry LineB { get; init; } = new(new(0, 10), new(1, 0));
    [DisplayName("原图坐标空间")] public string CoordinateSpaceId { get; init; } = "image";
    [DisplayName("平行角度容差 °")] public double ParallelToleranceDegrees { get; init; } = .1;
}
public sealed record AngleMeasureOptions
{
    [DisplayName("直线 A（未连端口时）")] public LineGeometry LineA { get; init; } = new(new(0, 0), new(1, 0));
    [DisplayName("直线 B（未连端口时）")] public LineGeometry LineB { get; init; } = new(new(0, 0), new(0, 1));
    [DisplayName("原图坐标空间")] public string CoordinateSpaceId { get; init; } = "image";
    [DisplayName("取两线最小夹角（0..90°）")] public bool UseAcuteAngle { get; init; } = true;
}

public static class DistanceMeasureProcessor
{
    public static IReadOnlyList<string> Validate(DistanceMeasureOptions p)
    {
        ArgumentNullException.ThrowIfNull(p); var errors = new List<string>();
        if (!Enum.IsDefined(p.Mode)) errors.Add("Invalid distance mode.");
        if (!MeasurementGeometry.Finite(p.PointA) || !MeasurementGeometry.Finite(p.PointB)) errors.Add("Point coordinates must be finite within ±1e9 pixels.");
        if (string.IsNullOrWhiteSpace(p.CoordinateSpaceId)) errors.Add("CoordinateSpaceId is required.");
        if (!double.IsFinite(p.ParallelToleranceDegrees) || p.ParallelToleranceDegrees < 0 || p.ParallelToleranceDegrees > 5) errors.Add("ParallelToleranceDegrees must be in [0,5].");
        if (p.Mode != DistanceMeasureMode.PointToPoint && !MeasurementGeometry.ValidLine(p.LineA, p.LineA?.CoordinateSpaceId ?? p.CoordinateSpaceId, out var a)) errors.Add("LineA: " + a);
        if (p.Mode == DistanceMeasureMode.ParallelLines && !MeasurementGeometry.ValidLine(p.LineB, p.LineB?.CoordinateSpaceId ?? p.CoordinateSpaceId, out var b)) errors.Add("LineB: " + b);
        return errors;
    }
    public static MeasurementResult Process(DistanceMeasureOptions options, Point2D? pointA = null, Point2D? pointB = null,
        LineGeometry? lineA = null, LineGeometry? lineB = null, CancellationToken cancellationToken = default)
    {
        var errors = Validate(options); if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors)); cancellationToken.ThrowIfCancellationRequested();
        var a = pointA ?? options.PointA; var b = pointB ?? options.PointB;
        if (!MeasurementGeometry.Finite(a) || !MeasurementGeometry.Finite(b)) return Fail("Point coordinates must be finite within ±1e9 pixels.");
        double distance; Point2D from, to;
        if (options.Mode == DistanceMeasureMode.PointToPoint)
        {
            from = a; to = b; distance = MeasurementGeometry.Length(b.X - a.X, b.Y - a.Y);
        }
        else
        {
            var first = lineA ?? options.LineA;
            if (!MeasurementGeometry.ValidLine(first, options.CoordinateSpaceId, out var error)) return Fail("LineA: " + error);
            if (options.Mode == DistanceMeasureMode.ParallelLines)
            {
                var second = lineB ?? options.LineB;
                if (!MeasurementGeometry.ValidLine(second, options.CoordinateSpaceId, out error)) return Fail("LineB: " + error);
                var angle = MeasurementGeometry.Angle(first.Direction, second.Direction, true);
                if (angle > options.ParallelToleranceDegrees + 1e-12) return Fail("Lines are not parallel within the configured angular tolerance.");
                a = second.Origin;
            }
            var norm = MeasurementGeometry.Length(first.Direction.X, first.Direction.Y);
            var ux = first.Direction.X / norm; var uy = first.Direction.Y / norm;
            var projection = (a.X - first.Origin.X) * ux + (a.Y - first.Origin.Y) * uy;
            from = a; to = new(first.Origin.X + projection * ux, first.Origin.Y + projection * uy);
            distance = MeasurementGeometry.Length(to.X - from.X, to.Y - from.Y);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return MeasurementGeometry.Result(true, "DistanceMeasure", options.CoordinateSpaceId, "px", new() { ["Distance"] = distance },
            [new(MeasurementOverlayKind.Line, $"Distance {distance:F3}px", Array.AsReadOnly(new[] { from, to }), options.CoordinateSpaceId)]);
        MeasurementResult Fail(string reason) => MeasurementGeometry.Result(false, "DistanceMeasure", options.CoordinateSpaceId, "px", new(), [], reason);
    }
}

public static class AngleMeasureProcessor
{
    public static IReadOnlyList<string> Validate(AngleMeasureOptions p)
    {
        ArgumentNullException.ThrowIfNull(p); var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(p.CoordinateSpaceId)) errors.Add("CoordinateSpaceId is required.");
        if (!MeasurementGeometry.ValidLine(p.LineA, p.LineA?.CoordinateSpaceId ?? p.CoordinateSpaceId, out var a)) errors.Add("LineA: " + a);
        if (!MeasurementGeometry.ValidLine(p.LineB, p.LineB?.CoordinateSpaceId ?? p.CoordinateSpaceId, out var b)) errors.Add("LineB: " + b);
        return errors;
    }
    public static MeasurementResult Process(AngleMeasureOptions options, LineGeometry? lineA = null, LineGeometry? lineB = null,
        CancellationToken cancellationToken = default)
    {
        var errors = Validate(options); if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors)); cancellationToken.ThrowIfCancellationRequested();
        var a = lineA ?? options.LineA; var b = lineB ?? options.LineB;
        if (!MeasurementGeometry.ValidLine(a, options.CoordinateSpaceId, out var error)) return Fail("LineA: " + error);
        if (!MeasurementGeometry.ValidLine(b, options.CoordinateSpaceId, out error)) return Fail("LineB: " + error);
        var angle = MeasurementGeometry.Angle(a.Direction, b.Direction, options.UseAcuteAngle);
        cancellationToken.ThrowIfCancellationRequested();
        MeasurementOverlay Overlay(LineGeometry line, string label)
        {
            var norm = MeasurementGeometry.Length(line.Direction.X, line.Direction.Y); var x = line.Direction.X / norm * 40; var y = line.Direction.Y / norm * 40;
            return new(MeasurementOverlayKind.Line, label, Array.AsReadOnly(new[] { new Point2D(line.Origin.X - x, line.Origin.Y - y), new Point2D(line.Origin.X + x, line.Origin.Y + y) }), options.CoordinateSpaceId);
        }
        return MeasurementGeometry.Result(true, "AngleMeasure", options.CoordinateSpaceId, "deg", new() { ["Angle"] = angle }, [Overlay(a, $"Angle {angle:F3}°"), Overlay(b, "Line B")]);
        MeasurementResult Fail(string reason) => MeasurementGeometry.Result(false, "AngleMeasure", options.CoordinateSpaceId, "deg", new(), [], reason);
    }
}

internal static class MeasurementGeometry
{
    internal static bool Finite(Point2D p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) <= 1e9 && Math.Abs(p.Y) <= 1e9;
    internal static double Length(double x, double y) => Math.Sqrt(x * x + y * y);
    internal static bool ValidLine(LineGeometry? line, string space, out string? error)
    {
        error = line is null ? "Line is missing." : string.IsNullOrWhiteSpace(line.CoordinateSpaceId) ? "Line coordinate space is missing." : !Finite(line.Origin) || !Finite(line.Direction) ? "Coordinates/direction must be finite within ±1e9."
            : line.CoordinateSpaceId != space ? "Line coordinate space differs from CoordinateSpaceId."
            : Length(line.Direction.X, line.Direction.Y) < 1e-12 ? "Line direction cannot be zero." : null;
        return error is null;
    }
    internal static double Angle(Point2D a, Point2D b, bool acute)
    {
        var an = Length(a.X, a.Y); var bn = Length(b.X, b.Y);
        var ax = a.X / an; var ay = a.Y / an; var bx = b.X / bn; var by = b.Y / bn;
        var angle = Math.Atan2(Math.Abs(ax * by - ay * bx), Math.Clamp(ax * bx + ay * by, -1, 1)) * 180 / Math.PI;
        return acute ? Math.Min(angle, 180 - angle) : angle;
    }
    internal static MeasurementResult Result(bool valid, string kind, string space, string unit, Dictionary<string, double> values, MeasurementOverlay[] overlays, string? error = null)
        => new(valid, kind, space, unit, new ReadOnlyDictionary<string, double>(values), Array.AsReadOnly(overlays), error);
}

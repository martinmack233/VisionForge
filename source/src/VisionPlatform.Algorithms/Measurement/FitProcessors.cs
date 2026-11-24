using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CvInspect.Vision;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;

namespace VisionPlatform.Algorithms.Measurement;

public sealed record LineFitOptions
{
    [DisplayName("剔除离群点")] public bool RejectOutliers { get; init; }
    [DisplayName("离群阈值 px"), Range(double.Epsilon, 1000000)] public double OutlierThreshold { get; init; } = 2;
    [DisplayName("最少内点"), Range(2, 100000)] public int MinimumInliers { get; init; } = 2;
    [DisplayName("RANSAC次数"), Range(1, 2048)] public int RansacTrials { get; init; } = 128;
}
public sealed record CircleFitOptions
{
    [DisplayName("剔除离群点")] public bool RejectOutliers { get; init; } = true;
    [DisplayName("离群阈值 px"), Range(double.Epsilon, 1000000)] public double OutlierThreshold { get; init; } = 1.5;
    [DisplayName("最少内点"), Range(3, 100000)] public int MinimumInliers { get; init; } = 6;
    [DisplayName("RANSAC次数"), Range(1, 2048)] public int RansacTrials { get; init; } = 256;
}
public sealed record LineFitOutput(LineGeometry? Line, MeasurementResult Result);
public sealed record CircleFitOutput(CircleGeometry? Circle, MeasurementResult Result);

public static class LineFitProcessor
{
    public static IReadOnlyList<string> Validate(LineFitOptions options) => FitSupport.Validate(options.OutlierThreshold, options.MinimumInliers, options.RansacTrials, 2);
    public static LineFitOutput Process(EdgePointSet input, LineFitOptions options, CancellationToken ct = default)
    {
        FitSupport.Check(input, Validate(options)); ct.ThrowIfCancellationRequested();
        var all = input.Points.ToArray(); var chosen = all;
        if (all.Length < options.MinimumInliers) return Fail("Not enough points.");
        if (options.RejectOutliers)
        {
            var random = new Random(1949); var bestCount = 0; var bestError = double.PositiveInfinity;
            for (var trial = 0; trial < options.RansacTrials; trial++)
            {
                ct.ThrowIfCancellationRequested(); var a = all[random.Next(all.Length)]; var b = all[random.Next(all.Length)];
                var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy); if (length < 1e-9) continue;
                var candidate = new LineGeometry(a, new(dx / length, dy / length), input.CoordinateSpaceId);
                var inliers = FitSupport.Inliers(all, p => Distance(p, candidate), options.OutlierThreshold, ct);
                var error = inliers.Sum(p => Math.Pow(Distance(p, candidate), 2));
                if (inliers.Length > bestCount || inliers.Length == bestCount && error < bestError) { chosen = inliers; bestCount = inliers.Length; bestError = error; }
            }
            if (bestCount < options.MinimumInliers) return Fail("Not enough line inliers after deterministic RANSAC.");
        }
        if (!Fit(chosen, input.CoordinateSpaceId, out var line)) return Fail("Coincident or degenerate line points.");
        if (options.RejectOutliers)
        {
            chosen = FitSupport.Inliers(all, p => Distance(p, line!), options.OutlierThreshold, ct);
            if (chosen.Length < options.MinimumInliers || !Fit(chosen, input.CoordinateSpaceId, out line)) return Fail("Line refit is degenerate.");
        }
        ct.ThrowIfCancellationRequested();
        var rms = Math.Sqrt(chosen.Average(p => Math.Pow(Distance(p, line!), 2)));
        var projections = chosen.Select(p => (p.X - line!.Origin.X) * line.Direction.X + (p.Y - line.Origin.Y) * line.Direction.Y).ToArray();
        Point2D At(double t) => new(line!.Origin.X + t * line.Direction.X, line.Origin.Y + t * line.Direction.Y);
        var values = new Dictionary<string, double> { ["Angle"] = Math.Atan2(line!.Direction.Y, line.Direction.X) * 180 / Math.PI,
            ["RmsError"] = rms, ["PointCount"] = chosen.Length, ["InputPointCount"] = all.Length };
        var overlay = new MeasurementOverlay(MeasurementOverlayKind.Line, $"Line RMS {rms:F3}px", Array.AsReadOnly(new[] { At(projections.Min()), At(projections.Max()) }), input.CoordinateSpaceId);
        return new(line, FitSupport.Result(true, "LineFit", input.CoordinateSpaceId, values, [overlay]));
        LineFitOutput Fail(string reason) => new(null, FitSupport.Result(false, "LineFit", input.CoordinateSpaceId, new() { ["PointCount"] = 0 }, [], reason));
    }
    private static bool Fit(Point2D[] points, string space, out LineGeometry? line)
    {
        line = null; if (points.Length < 2) return false;
        var mx = points.Average(p => p.X); var my = points.Average(p => p.Y);
        var scale = points.Max(p => Math.Max(Math.Abs(p.X - mx), Math.Abs(p.Y - my))); if (scale < 1e-9) return false;
        var fit = CvFit.Line(points.Select(p => ((p.X - mx) / scale, (p.Y - my) / scale)).ToArray());
        var ux = fit.Ux; var uy = fit.Uy; if (ux < 0 || Math.Abs(ux) < 1e-12 && uy < 0) { ux = -ux; uy = -uy; }
        line = new(new(mx + fit.Mx * scale, my + fit.My * scale), new(ux, uy), space); return true;
    }
    private static double Distance(Point2D point, LineGeometry line) => Math.Abs((point.X - line.Origin.X) * line.Direction.Y - (point.Y - line.Origin.Y) * line.Direction.X);
}

public static class CircleFitProcessor
{
    public static IReadOnlyList<string> Validate(CircleFitOptions options) => FitSupport.Validate(options.OutlierThreshold, options.MinimumInliers, options.RansacTrials, 3);
    public static CircleFitOutput Process(EdgePointSet input, CircleFitOptions options, CancellationToken ct = default)
    {
        FitSupport.Check(input, Validate(options)); ct.ThrowIfCancellationRequested();
        var all = input.Points.ToArray(); var chosen = all;
        if (all.Length < options.MinimumInliers) return Fail("Not enough circle points.");
        if (options.RejectOutliers)
        {
            var random = new Random(1949); var bestCount = 0; var bestError = double.PositiveInfinity;
            for (var trial = 0; trial < options.RansacTrials; trial++)
            {
                ct.ThrowIfCancellationRequested();
                Point2D[] sample = [all[random.Next(all.Length)], all[random.Next(all.Length)], all[random.Next(all.Length)]];
                if (!Fit(sample, input.CoordinateSpaceId, out var candidate)) continue;
                var inliers = FitSupport.Inliers(all, p => Residual(p, candidate!), options.OutlierThreshold, ct);
                var error = inliers.Sum(p => Math.Pow(Residual(p, candidate!), 2));
                if (inliers.Length > bestCount || inliers.Length == bestCount && error < bestError) { chosen = inliers; bestCount = inliers.Length; bestError = error; }
            }
            if (bestCount < options.MinimumInliers) return Fail("Not enough circle inliers after deterministic RANSAC.");
        }
        if (!Fit(chosen, input.CoordinateSpaceId, out var circle)) return Fail("Collinear, coincident or degenerate circle points.");
        if (options.RejectOutliers)
        {
            chosen = FitSupport.Inliers(all, p => Residual(p, circle!), options.OutlierThreshold, ct);
            if (chosen.Length < options.MinimumInliers || !Fit(chosen, input.CoordinateSpaceId, out circle)) return Fail("Circle refit is degenerate.");
        }
        ct.ThrowIfCancellationRequested();
        var rms = Math.Sqrt(chosen.Average(p => Math.Pow(Residual(p, circle!), 2)));
        var values = new Dictionary<string, double> { ["CenterX"] = circle!.Center.X, ["CenterY"] = circle.Center.Y, ["Radius"] = circle.Radius,
            ["Diameter"] = circle.Radius * 2, ["RmsError"] = rms, ["PointCount"] = chosen.Length, ["InputPointCount"] = all.Length };
        var overlay = new MeasurementOverlay(MeasurementOverlayKind.Circle, $"R={circle.Radius:F3}px RMS={rms:F3}px", Array.AsReadOnly(new[] { circle.Center }), input.CoordinateSpaceId, circle.Radius);
        return new(circle, FitSupport.Result(true, "CircleFit", input.CoordinateSpaceId, values, [overlay]));
        CircleFitOutput Fail(string reason) => new(null, FitSupport.Result(false, "CircleFit", input.CoordinateSpaceId, new() { ["PointCount"] = 0 }, [], reason));
    }
    private static bool Fit(Point2D[] points, string space, out CircleGeometry? circle)
    {
        circle = null; if (points.Length < 3) return false;
        var mx = points.Average(p => p.X); var my = points.Average(p => p.Y);
        var scale = points.Max(p => Math.Max(Math.Abs(p.X - mx), Math.Abs(p.Y - my))); if (scale < 1e-9) return false;
        // Reuse audited Kasa least squares after centering/scaling, reducing cancellation at large coordinates.
        var fit = CvFit.Circle(points.Select(p => ((p.X - mx) / scale, (p.Y - my) / scale)).ToArray());
        if (fit is null || !double.IsFinite(fit.Value.R) || fit.Value.R <= 0) return false;
        var x = mx + fit.Value.Cx * scale; var y = my + fit.Value.Cy * scale; var r = fit.Value.R * scale;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(r)) return false;
        circle = new(new(x, y), r, space); return true;
    }
    private static double Residual(Point2D p, CircleGeometry circle) => Math.Abs(Math.Sqrt(Math.Pow(p.X - circle.Center.X, 2) + Math.Pow(p.Y - circle.Center.Y, 2)) - circle.Radius);
}

internal static class FitSupport
{
    internal static IReadOnlyList<string> Validate(double threshold, int minimum, int trials, int required)
    {
        var errors = new List<string>();
        if (!double.IsFinite(threshold) || threshold <= 0 || threshold > 1000000) errors.Add("OutlierThreshold must be finite in (0,1000000].");
        if (minimum < required || minimum > 100000) errors.Add($"MinimumInliers must be in [{required},100000].");
        if (trials < 1 || trials > 2048) errors.Add("RansacTrials must be in [1,2048].");
        return errors;
    }
    internal static void Check(EdgePointSet input, IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        if (input.Points is null || input.Points.Count > 100000 || string.IsNullOrWhiteSpace(input.CoordinateSpaceId)) throw new ArgumentException("Points require a coordinate space and at most 100000 samples.");
        if (input.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || Math.Abs(p.X) > 1e9 || Math.Abs(p.Y) > 1e9)) throw new ArgumentException("Point coordinates must be finite within ±1e9 pixels.");
    }
    internal static Point2D[] Inliers(Point2D[] points, Func<Point2D, double> error, double threshold, CancellationToken ct)
    {
        var accepted = new List<Point2D>();
        for (var i = 0; i < points.Length; i++) { if ((i & 255) == 0) ct.ThrowIfCancellationRequested(); if (error(points[i]) <= threshold) accepted.Add(points[i]); }
        return accepted.ToArray();
    }
    internal static MeasurementResult Result(bool valid, string kind, string space, Dictionary<string, double> values, MeasurementOverlay[] overlays, string? reason = null)
        => new(valid, kind, space, "px", new ReadOnlyDictionary<string, double>(values), Array.AsReadOnly(overlays), reason);
}

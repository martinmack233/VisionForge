using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Measurement;

public enum EdgePolarity { Either, DarkToLight, LightToDark }
public enum EdgeSelection { Strongest, First, Last }
public sealed record CaliperOptions
{
    [DisplayName("搜索中心 X")] public double CenterX { get; init; } = 100;
    [DisplayName("搜索中心 Y")] public double CenterY { get; init; } = 100;
    [DisplayName("搜索方向 °")] public double SearchDirectionDegrees { get; init; }
    [DisplayName("搜索长度 px"), Range(6, 4096)] public int SearchLength { get; init; } = 40;
    [DisplayName("投影宽度 px"), Range(1, 256)] public int ProjectionWidth { get; init; } = 5;
    [DisplayName("卡尺数量"), Range(1, 2048)] public int CaliperCount { get; init; } = 16;
    [DisplayName("排列跨度 px"), Range(0, 1000000)] public double Span { get; init; } = 100;
    [DisplayName("边缘强度阈值"), Range(double.Epsilon, 255)] public double ContrastThreshold { get; init; } = 10;
    [DisplayName("边缘极性")] public EdgePolarity Polarity { get; init; } = EdgePolarity.Either;
    [DisplayName("边缘选择")] public EdgeSelection Selection { get; init; } = EdgeSelection.Strongest;
    [DisplayName("最少有效点"), Range(1, 2048)] public int MinimumValidPoints { get; init; } = 1;
}
public sealed record CircleEdgePointsOptions
{
    [DisplayName("圆心 X")] public double CenterX { get; init; } = 100;
    [DisplayName("圆心 Y")] public double CenterY { get; init; } = 100;
    [DisplayName("参考半径 px"), Range(double.Epsilon, 1000000)] public double Radius { get; init; } = 50;
    [DisplayName("径向搜索长度 px"), Range(6, 4096)] public int SearchLength { get; init; } = 30;
    [DisplayName("切向投影宽度 px"), Range(1, 256)] public int ProjectionWidth { get; init; } = 3;
    [DisplayName("采样数量"), Range(3, 2048)] public int SampleCount { get; init; } = 64;
    [DisplayName("起始角 °")] public double StartAngle { get; init; }
    [DisplayName("圆弧角 °"), Range(double.Epsilon, 360)] public double SweepAngle { get; init; } = 360;
    [DisplayName("向内搜索")] public bool SearchInward { get; init; }
    [DisplayName("边缘强度阈值"), Range(double.Epsilon, 255)] public double ContrastThreshold { get; init; } = 10;
    [DisplayName("边缘极性")] public EdgePolarity Polarity { get; init; } = EdgePolarity.Either;
    [DisplayName("边缘选择")] public EdgeSelection Selection { get; init; } = EdgeSelection.Strongest;
    [DisplayName("最少有效点"), Range(1, 2048)] public int MinimumValidPoints { get; init; } = 3;
}
public sealed record CaliperOutput(EdgePointSet Points, MeasurementResult Result);

public static class CaliperProcessor
{
    public static IReadOnlyList<string> Validate(CaliperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = CaliperSupport.Validate(options.CenterX, options.CenterY, options.SearchLength, options.ProjectionWidth,
            options.CaliperCount, options.ContrastThreshold, options.Polarity, options.Selection, options.MinimumValidPoints);
        if (!double.IsFinite(options.SearchDirectionDegrees)) errors.Add("SearchDirectionDegrees must be finite.");
        if (!double.IsFinite(options.Span) || options.Span < 0 || options.Span > 1000000) errors.Add("Span must be finite and in [0,1000000].");
        return errors;
    }
    public static CaliperOutput Process(ImageFrame image, CaliperOptions options, CancellationToken ct = default)
    {
        CaliperSupport.Check(image, Validate(options)); ct.ThrowIfCancellationRequested();
        using var lease = new OpenCvImageLease(image);
        var rad = (options.SearchDirectionDegrees % 360) * Math.PI / 180;
        var points = new List<Point2D>(); var strengths = new List<double>();
        for (var index = 0; index < options.CaliperCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = options.CaliperCount == 1 ? 0 : options.Span * (index / (double)(options.CaliperCount - 1) - .5);
            var cx = options.CenterX - Math.Sin(rad) * offset;
            var cy = options.CenterY + Math.Cos(rad) * offset;
            CaliperSupport.Sample(lease, image, cx, cy, options.SearchDirectionDegrees, options.SearchLength, options.ProjectionWidth,
                options.Polarity, options.Selection, options.ContrastThreshold, points, strengths);
        }
        return CaliperSupport.Output(image, points, strengths, options.MinimumValidPoints, "Caliper");
    }
}

public static class CircleEdgePointsProcessor
{
    public static IReadOnlyList<string> Validate(CircleEdgePointsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = CaliperSupport.Validate(options.CenterX, options.CenterY, options.SearchLength, options.ProjectionWidth,
            options.SampleCount, options.ContrastThreshold, options.Polarity, options.Selection, options.MinimumValidPoints);
        if (!double.IsFinite(options.Radius) || options.Radius <= 0 || options.Radius > 1000000) errors.Add("Radius must be finite and in (0,1000000].");
        if (!double.IsFinite(options.StartAngle)) errors.Add("StartAngle must be finite.");
        if (!double.IsFinite(options.SweepAngle) || options.SweepAngle <= 0 || options.SweepAngle > 360) errors.Add("SweepAngle must be in (0,360].");
        if (options.SampleCount < 3) errors.Add("SampleCount must be at least 3.");
        return errors;
    }
    public static CaliperOutput Process(ImageFrame image, CircleEdgePointsOptions options, CancellationToken ct = default)
    {
        CaliperSupport.Check(image, Validate(options)); ct.ThrowIfCancellationRequested();
        using var lease = new OpenCvImageLease(image);
        var points = new List<Point2D>(); var strengths = new List<double>();
        for (var index = 0; index < options.SampleCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            // A complete circle excludes the duplicated end point; an arc includes both ends.
            var denominator = options.SweepAngle == 360 ? options.SampleCount : options.SampleCount - 1;
            var angle = options.StartAngle % 360 + options.SweepAngle * index / denominator;
            var rad = angle * Math.PI / 180;
            CaliperSupport.Sample(lease, image, options.CenterX + options.Radius * Math.Cos(rad), options.CenterY + options.Radius * Math.Sin(rad),
                angle + (options.SearchInward ? 180 : 0), options.SearchLength, options.ProjectionWidth,
                options.Polarity, options.Selection, options.ContrastThreshold, points, strengths);
        }
        return CaliperSupport.Output(image, points, strengths, options.MinimumValidPoints, "CircleEdgePoints");
    }
}

internal static class CaliperSupport
{
    internal static List<string> Validate(double cx, double cy, int length, int projection, int count, double contrast,
        EdgePolarity polarity, EdgeSelection selection, int minimum)
    {
        var errors = new List<string>();
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || Math.Abs(cx) > 1e9 || Math.Abs(cy) > 1e9) errors.Add("Search center must be finite within ±1e9 pixels.");
        if (length < 6 || length > 4096) errors.Add("SearchLength must be in [6,4096].");
        if (projection < 1 || projection > 256) errors.Add("ProjectionWidth must be in [1,256].");
        if (count < 1 || count > 2048) errors.Add("Caliper/sample count must be in [1,2048].");
        if ((long)length * projection * count > 16_000_000) errors.Add("Caliper sampling budget exceeds 16000000 samples.");
        if (!double.IsFinite(contrast) || contrast <= 0 || contrast > 255) errors.Add("ContrastThreshold must be in (0,255].");
        if (!Enum.IsDefined(polarity) || !Enum.IsDefined(selection)) errors.Add("Invalid edge polarity or selection.");
        if (minimum < 1 || minimum > count) errors.Add("MinimumValidPoints must be between 1 and the sample count.");
        return errors;
    }
    internal static void Check(ImageFrame image, IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        if (image.PixelFormat != PixelFormat.Mono8) throw new ArgumentException("Caliper requires Mono8. Convert RGB/BGR/Bayer explicitly before measurement.");
    }
    internal static void Sample(OpenCvImageLease lease, ImageFrame image, double cx, double cy, double angle, int length, int projection,
        EdgePolarity polarity, EdgeSelection selection, double contrast, List<Point2D> points, List<double> strengths)
    {
        var result = CvCaliper.Run(lease.Mat, new CvCaliperOpt
        {
            CenterX = cx - image.CoordinateSpace.OriginX, CenterY = cy - image.CoordinateSpace.OriginY,
            SearchDirDeg = angle % 360, SearchLength = length, ProjectionLength = projection, ContrastThreshold = contrast,
            Polarity = polarity switch { EdgePolarity.DarkToLight => CvEdgePolarity.DarkToLight, EdgePolarity.LightToDark => CvEdgePolarity.LightToDark, _ => CvEdgePolarity.Either },
            EdgeSelect = selection switch { EdgeSelection.First => CvEdgeSelect.First, EdgeSelection.Last => CvEdgeSelect.Last, _ => CvEdgeSelect.Best }
        });
        if (result is null) return;
        var x = result.Edge0.X; var y = result.Edge0.Y;
        // The upstream sampler can replicate partial outside profiles. Retain only physically observed points.
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= image.Width - 1 || y >= image.Height - 1) return;
        points.Add(new(x + image.CoordinateSpace.OriginX, y + image.CoordinateSpace.OriginY)); strengths.Add(result.Edge0.Contrast);
    }
    internal static CaliperOutput Output(ImageFrame image, List<Point2D> points, List<double> strengths, int minimum, string kind)
    {
        var valid = points.Count >= minimum;
        var space = image.CoordinateSpace.Id;
        // A failed measurement deliberately publishes an empty current point set.
        var output = new EdgePointSet(Array.AsReadOnly(valid ? points.ToArray() : []), space);
        var values = new Dictionary<string, double> { ["EdgeCount"] = valid ? points.Count : 0 };
        if (valid) { values["MeanX"] = points.Average(p => p.X); values["MeanY"] = points.Average(p => p.Y); values["MeanStrength"] = strengths.Average(); }
        var overlay = valid ? new[] { new MeasurementOverlay(MeasurementOverlayKind.Points, $"{kind}: {points.Count} edges", output.Points, space) } : [];
        return new(output, new(valid, kind, space, "px", new ReadOnlyDictionary<string, double>(values), Array.AsReadOnly(overlay),
            valid ? null : $"Found {points.Count} valid edges; at least {minimum} required. Check search region, polarity and contrast."));
    }
}

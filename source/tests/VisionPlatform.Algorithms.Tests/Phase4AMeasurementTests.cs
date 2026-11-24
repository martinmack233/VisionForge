using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase4AMeasurementTests
{
    [Theory]
    [InlineData(60.15)]
    [InlineData(60.35)]
    [InlineData(60.65)]
    public void CaliperLocatesKnownAnalyticFractionalEdgeInsteadOfIntegerCast(double edge)
    {
        using var frame = Image(160, 160, (x, _) => 20 + 220 / (1 + Math.Exp(-(x - edge) / 1.3)));
        var result = CaliperProcessor.Process(frame, new() { CenterX = 60, CenterY = 80, Span = 100 });
        Assert.True(result.Result.Valid, result.Result.ErrorReason); Assert.Equal(16, result.Points.Points.Count);
        foreach (var point in result.Points.Points)
        {
            Assert.InRange(Math.Abs(point.X - edge), 0, .12);
            Assert.True(Math.Abs(point.X - Math.Round(point.X)) > .05);
        }
        Assert.Equal("px", result.Result.Unit);
    }

    [Fact]
    public void OriginStrideAndLocalViewAreMeasuredInOriginalPixelCentersWithoutChangingSource()
    {
        using var frame = Image(180, 170, (x, _) => 20 + 220 / (1 + Math.Exp(-(x - 80.35) / 1.3)), new("sensor", 1000, 2000), 193);
        var before = Bytes(frame); using var view = frame.CreateView(20, 10, 130, 140);
        var result = CaliperProcessor.Process(view, new() { CenterX = 1080, CenterY = 2080, Span = 80 });
        Assert.True(result.Result.Valid); Assert.Equal("sensor", result.Points.CoordinateSpaceId);
        Assert.InRange(result.Result.Values["MeanX"], 1080.23, 1080.47);
        Assert.InRange(result.Result.Values["MeanY"], 2079.99, 2080.01); Assert.Equal(before, Bytes(frame));
    }

    [Fact]
    public void SearchDirectionAndPolarityHavePhysicalMeaning()
    {
        using var frame = Image(160, 160, (_, y) => 20 + 220 / (1 + Math.Exp(-(y - 80.35) / 1.3)));
        var p = new CaliperOptions { CenterX = 80, CenterY = 80, SearchDirectionDegrees = 90, Polarity = EdgePolarity.DarkToLight };
        var forward = CaliperProcessor.Process(frame, p);
        Assert.True(forward.Result.Valid); Assert.InRange(forward.Result.Values["MeanY"], 80.23, 80.47);
        Assert.False(CaliperProcessor.Process(frame, p with { Polarity = EdgePolarity.LightToDark }).Result.Valid);
        Assert.True(CaliperProcessor.Process(frame, p with { SearchDirectionDegrees = -90, Polarity = EdgePolarity.LightToDark }).Result.Valid);
    }

    [Theory]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void MeasurementNeverImplicitlyChangesInputFormat(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(100, 100, format); using var image = builder.Build();
        Assert.Throws<ArgumentException>(() => CaliperProcessor.Process(image, new()));
        Assert.Throws<ArgumentException>(() => CircleEdgePointsProcessor.Process(image, new()));
    }

    [Fact]
    public void BlankAndOutsideSearchProduceCurrentEmptyResults()
    {
        using var frame = Image(160, 160, (_, _) => 40);
        var result = CaliperProcessor.Process(frame, new() { CenterX = 80, CenterY = 80 });
        Assert.False(result.Result.Valid); Assert.Empty(result.Points.Points); Assert.Empty(result.Result.Overlays); Assert.NotEmpty(result.Result.ErrorReason!);
        var outside = CaliperProcessor.Process(frame, new() { CenterX = -10000, CenterY = 80 });
        Assert.False(outside.Result.Valid); Assert.Empty(outside.Points.Points);
    }

    [Fact]
    public void InvalidCaliperParametersRejectBeforeNativeAccess()
    {
        Assert.NotEmpty(CaliperProcessor.Validate(new() { SearchLength = 0 }));
        Assert.NotEmpty(CaliperProcessor.Validate(new() { ProjectionWidth = 0 }));
        Assert.NotEmpty(CaliperProcessor.Validate(new() { CenterX = double.NaN }));
        Assert.NotEmpty(CaliperProcessor.Validate(new() { SearchLength = 4096, ProjectionWidth = 256, CaliperCount = 2048 }));
        Assert.NotEmpty(CircleEdgePointsProcessor.Validate(new() { SweepAngle = double.PositiveInfinity }));
        Assert.NotEmpty(CircleEdgePointsProcessor.Validate(new() { Radius = -1 }));
        using var frame = Image(20, 20, (_, _) => 0);
        Assert.Throws<ArgumentException>(() => CaliperProcessor.Process(frame, new() { Polarity = (EdgePolarity)99 }));
    }

    [Fact]
    public void AnalyticCircleEdgePointsFitKnownSubpixelCircle()
    {
        const double cx = 127.4, cy = 129.2, radius = 60.35;
        using var frame = Image(256, 256, (x, y) => 20 + 220 / (1 + Math.Exp((Math.Sqrt(Math.Pow(x - cx, 2) + Math.Pow(y - cy, 2)) - radius) / 1.3)));
        var edges = CircleEdgePointsProcessor.Process(frame, new() { CenterX = cx, CenterY = cy, Radius = 60, Polarity = EdgePolarity.LightToDark });
        Assert.True(edges.Result.Valid); Assert.Equal(64, edges.Points.Points.Count);
        var fit = CircleFitProcessor.Process(edges.Points, new());
        Assert.True(fit.Result.Valid, fit.Result.ErrorReason); Assert.NotNull(fit.Circle);
        Assert.InRange(Math.Abs(fit.Circle!.Center.X - cx), 0, .1); Assert.InRange(Math.Abs(fit.Circle.Center.Y - cy), 0, .1);
        Assert.InRange(Math.Abs(fit.Circle.Radius - radius), 0, .15); Assert.InRange(fit.Result.Values["RmsError"], 0, .15);
        Assert.Equal(MeasurementOverlayKind.Circle, Assert.Single(fit.Result.Overlays).Kind);
    }

    [Fact]
    public void LineTotalLeastSquaresFitsVerticalAndTranslatedPoints()
    {
        var points = new EdgePointSet(Array.AsReadOnly(Enumerable.Range(0, 12).Select(i => new Point2D(100000080.25, 100000030 + i)).ToArray()), "sensor");
        var fit = LineFitProcessor.Process(points, new());
        Assert.True(fit.Result.Valid); Assert.Equal("sensor", fit.Line!.CoordinateSpaceId);
        Assert.InRange(Math.Abs(fit.Line.Origin.X - 100000080.25), 0, 1e-7);
        Assert.InRange(Math.Abs(fit.Result.Values["Angle"] - 90), 0, 1e-7); Assert.InRange(fit.Result.Values["RmsError"], 0, 1e-7);
        Assert.Equal(12, fit.Result.Values["PointCount"]);
    }

    [Fact]
    public void RobustLineRejectsOutliersAndIsRepeatableWithoutMutatingInputs()
    {
        var data = Enumerable.Range(0, 30).Select(i => new Point2D(i, i * .5 + 4)).Concat([new(10, 100), new(20, -80), new(-70, 50)]).ToArray();
        var original = data.ToArray(); var input = new EdgePointSet(Array.AsReadOnly(data));
        var options = new LineFitOptions { RejectOutliers = true, OutlierThreshold = .1, MinimumInliers = 20 };
        var first = LineFitProcessor.Process(input, options); var second = LineFitProcessor.Process(input, options);
        Assert.True(first.Result.Valid); Assert.Equal(30, first.Result.Values["PointCount"]);
        Assert.InRange(Math.Abs(first.Result.Values["Angle"] - Math.Atan(.5) * 180 / Math.PI), 0, 1e-7);
        Assert.Equal(first.Line, second.Line); Assert.Equal(first.Result.Values["RmsError"], second.Result.Values["RmsError"]); Assert.Equal(original, data);
    }

    [Fact]
    public void RobustCircleHandlesOutliersAndLargeOriginalCoordinates()
    {
        var points = Enumerable.Range(0, 72).Select(i => new Point2D(1e8 + 50 * Math.Cos(i * Math.PI / 36), 2e8 + 50 * Math.Sin(i * Math.PI / 36)))
            .Concat([new(1e8 + 200, 2e8), new(1e8, 2e8 - 150), new(1e8 - 100, 2e8 + 200)]).ToArray();
        var result = CircleFitProcessor.Process(new(Array.AsReadOnly(points), "world-pixels"), new() { OutlierThreshold = .1, MinimumInliers = 60 });
        Assert.True(result.Result.Valid, result.Result.ErrorReason); Assert.Equal(72, result.Result.Values["PointCount"]);
        Assert.InRange(Math.Abs(result.Circle!.Center.X - 1e8), 0, 1e-6); Assert.InRange(Math.Abs(result.Circle.Center.Y - 2e8), 0, 1e-6);
        Assert.InRange(Math.Abs(result.Circle.Radius - 50), 0, 1e-6); Assert.Equal("world-pixels", result.Circle.CoordinateSpaceId);
    }

    [Fact]
    public void CircleFitReportsActualResidualOnDeterministicNoisySamples()
    {
        var samples = Enumerable.Range(0, 90).Select(i =>
        {
            var angle = i * 2 * Math.PI / 90; var radius = 40 + .2 * Math.Cos(i * 13);
            return new Point2D(90 + radius * Math.Cos(angle), 100 + radius * Math.Sin(angle));
        }).Concat([new(20, 10), new(240, 100)]).ToArray();
        var fit = CircleFitProcessor.Process(new(Array.AsReadOnly(samples)), new() { MinimumInliers = 60 });
        Assert.True(fit.Result.Valid); Assert.Equal(90, fit.Result.Values["PointCount"]);
        Assert.InRange(Math.Abs(fit.Circle!.Center.X - 90), 0, .05); Assert.InRange(Math.Abs(fit.Circle.Center.Y - 100), 0, .05);
        Assert.InRange(Math.Abs(fit.Circle.Radius - 40), 0, .05); Assert.InRange(fit.Result.Values["RmsError"], .1, .2);
    }

    [Fact]
    public void DegenerateFitsNeverPublishInventedGeometry()
    {
        var same = new EdgePointSet(Array.AsReadOnly(new Point2D[] { new(4, 4), new(4, 4), new(4, 4) }));
        Assert.False(LineFitProcessor.Process(same, new()).Result.Valid);
        var circle = CircleFitProcessor.Process(new(Array.AsReadOnly(new Point2D[] { new(0, 0), new(1, 1), new(2, 2) })), new() { MinimumInliers = 3 });
        Assert.False(circle.Result.Valid); Assert.Null(circle.Circle); Assert.Empty(circle.Result.Overlays); Assert.NotEmpty(circle.Result.ErrorReason!);
        Assert.Throws<ArgumentException>(() => LineFitProcessor.Process(new([new(double.NaN, 0)]), new()));
        Assert.NotEmpty(CircleFitProcessor.Validate(new() { MinimumInliers = 2 }));
    }

    [Fact]
    public void CancelledMeasurementReleasesBorrowedLeaseAndDoesNotDisposeCallerImage()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var frame = Image(160, 160, (x, _) => x))
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => CaliperProcessor.Process(frame, new(), cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => CircleEdgePointsProcessor.Process(frame, new(), cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => LineFitProcessor.Process(new([new(0, 0), new(1, 1)]), new(), cancelled.Token));
            using var lease = frame.Lease(); Assert.Equal(40, lease.GetRowSpan(30)[40]);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task RealRuntimeRunsCaliperIntoLineAndFailedNextRunDoesNotReuseOldPoints()
    {
        using var image = Image(160, 160, (x, _) => 20 + 220 / (1 + Math.Exp(-(x - 60.35) / 1.3)));
        var registry = BuiltinRegistration.CreateRegistry();
        var spec = new NodeSpecification("caliper", "Caliper", "Caliper", JsonDefaults.Element(new CaliperOptions { CenterX = 60, CenterY = 80 }));
        var fitSpec = new NodeSpecification("fit", "LineFit", "LineFit", JsonDefaults.Element(new LineFitOptions()));
        var executor = new RuntimeExecutor(registry);
        using var successful = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image }, ".");
        Assert.True(successful.Success); var points = successful.Nodes["caliper"].Output<EdgePointSet>("Points"); Assert.Equal(16, points.Points.Count);
        using var fitted = await executor.ExecuteSingleAsync(fitSpec, new Dictionary<string, object> { ["Points"] = points }, ".");
        Assert.True(fitted.Success); Assert.True(fitted.Nodes["fit"].Output<bool>("Valid"));
        using var blank = Image(160, 160, (_, _) => 20);
        using var failed = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = blank }, ".");
        Assert.True(failed.Success); Assert.False(failed.Nodes["caliper"].Output<bool>("Valid")); Assert.Empty(failed.Nodes["caliper"].Output<EdgePointSet>("Points").Points);
    }

    private static ImageFrame Image(int width, int height, Func<double, double, double> intensity, ImageCoordinateSpace? space = null, int? stride = null)
    {
        using var builder = new ImageFrameBuilder(width, height, PixelFormat.Mono8, stride, space);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) builder.GetRowSpan(y)[x] = (byte)Math.Clamp(Math.Round(intensity(x, y)), 0, 255);
        return builder.Build();
    }
    private static byte[] Bytes(ImageFrame image)
    {
        using var lease = image.Lease(); var rows = new List<byte>(); for (var y = 0; y < image.Height; y++) rows.AddRange(lease.GetRowSpan(y).ToArray()); return rows.ToArray();
    }
}

using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase4ADistanceAngleTests
{
    [Fact]
    public void ConnectedGeometryUsesItsDeclaredSpaceWithoutRequiringUnusedDefaultGeometryToChange()
    {
        var horizontal = new LineGeometry(new(0, 0), new(1, 0), "crop");
        var vertical = new LineGeometry(new(0, 0), new(0, 1), "crop");
        var distance = DistanceMeasureProcessor.Process(new() { Mode = DistanceMeasureMode.PointToLine, CoordinateSpaceId = "crop" }, new(5, 9), lineA: horizontal);
        Assert.True(distance.Valid); Assert.Equal(9, distance.Values["Distance"]);
        var angle = AngleMeasureProcessor.Process(new() { CoordinateSpaceId = "crop" }, horizontal, vertical);
        Assert.True(angle.Valid); Assert.Equal(90, angle.Values["Angle"]);
        Assert.False(AngleMeasureProcessor.Process(new() { CoordinateSpaceId = "crop" }, horizontal).Valid);
    }

    [Fact]
    public void PointDistanceUsesActualCoordinateInputsAndPixels()
    {
        var value = DistanceMeasureProcessor.Process(new(), new(-4, 7), new(-1, 11));
        Assert.True(value.Valid); Assert.Equal(5, value.Values["Distance"], 10); Assert.Equal("px", value.Unit);
        Assert.Equal(new Point2D(-4, 7), Assert.Single(value.Overlays).Points[0]);
    }
    [Fact]
    public void PointToNonUnitDirectionUsesOrthogonalDistanceAndFoot()
    {
        var p = new DistanceMeasureOptions { Mode = DistanceMeasureMode.PointToLine };
        var result = DistanceMeasureProcessor.Process(p, new(5, 9), lineA: new(new(2, 1), new(0, 7)));
        Assert.True(result.Valid); Assert.Equal(3, result.Values["Distance"], 10);
        Assert.Equal(new Point2D(2, 9), Assert.Single(result.Overlays).Points[1]);
    }
    [Fact]
    public void ParallelDistanceAcceptsAntiParallelDirectionAndRejectsNonParallel()
    {
        var p = new DistanceMeasureOptions { Mode = DistanceMeasureMode.ParallelLines };
        var result = DistanceMeasureProcessor.Process(p, lineA: new(new(0, 0), new(1, 0)), lineB: new(new(100, 12), new(-8, 0)));
        Assert.True(result.Valid); Assert.Equal(12, result.Values["Distance"], 10);
        var bad = DistanceMeasureProcessor.Process(p, lineB: new(new(100, 12), new(1, 1)));
        Assert.False(bad.Valid); Assert.Empty(bad.Values); Assert.Empty(bad.Overlays); Assert.Contains("parallel", bad.ErrorReason!);
    }
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 90)]
    [InlineData(1, 1, 45)]
    [InlineData(-1, 1, 45)]
    [InlineData(-1, 0, 0)]
    public void SmallestUndirectedAngleIsCorrect(double x, double y, double expected)
    {
        var result = AngleMeasureProcessor.Process(new(), lineB: new(new(0, 0), new(x, y)));
        Assert.True(result.Valid); Assert.Equal(expected, result.Values["Angle"], 9); Assert.Equal("deg", result.Unit);
    }
    [Fact]
    public void OptionalDirectedLineConventionReturnsObtuseAngle()
    {
        var result = AngleMeasureProcessor.Process(new() { UseAcuteAngle = false }, lineB: new(new(0, 0), new(-1, 1)));
        Assert.Equal(135, result.Values["Angle"], 9);
    }
    [Fact]
    public void MissingCoordinateSpaceAndIllegalParametersAreRejected()
    {
        Assert.NotEmpty(DistanceMeasureProcessor.Validate(new() { Mode = (DistanceMeasureMode)999 }));
        Assert.NotEmpty(DistanceMeasureProcessor.Validate(new() { ParallelToleranceDegrees = double.NaN }));
        Assert.NotEmpty(AngleMeasureProcessor.Validate(new() { LineA = new(new(0, 0), new(0, 0)) }));
        Assert.NotEmpty(AngleMeasureProcessor.Validate(new() { CoordinateSpaceId = "" }));
        Assert.Throws<ArgumentException>(() => DistanceMeasureProcessor.Process(new() { PointA = new(double.NaN, 0) }));
    }
    [Fact]
    public void ConnectedInvalidGeometryReturnsCurrentInvalidResult()
    {
        var options = new DistanceMeasureOptions { Mode = DistanceMeasureMode.PointToLine };
        var bad = DistanceMeasureProcessor.Process(options, lineA: new(new(0, 0), new(1, 0), "different-image"));
        Assert.False(bad.Valid); Assert.Contains("space", bad.ErrorReason!); Assert.Empty(bad.Overlays);
        var zero = AngleMeasureProcessor.Process(new(), lineA: new(new(0, 0), new(0, 0)));
        Assert.False(zero.Valid); Assert.Contains("zero", zero.ErrorReason!); Assert.Empty(zero.Values);
        var nonfinite = DistanceMeasureProcessor.Process(new(), pointA: new(double.PositiveInfinity, 0));
        Assert.False(nonfinite.Valid);
    }
    [Fact]
    public void MeasurementsSupportCancellationWithoutOwnedResources()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => DistanceMeasureProcessor.Process(new(), cancellationToken: cancel.Token));
        Assert.Throws<OperationCanceledException>(() => AngleMeasureProcessor.Process(new(), cancellationToken: cancel.Token));
    }
    [Fact]
    public async Task RuntimeConnectedGeometryAndMeasurementDisplayUseActualValues()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var executor = new RuntimeExecutor(registry);
        var spec = new NodeSpecification("distance", "DistanceMeasure", "Distance", JsonDefaults.Element(new DistanceMeasureOptions { Mode = DistanceMeasureMode.PointToLine }));
        using var first = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["PointA"] = new Point2D(3, 8), ["LineA"] = new LineGeometry(new(0, 0), new(1, 0)) });
        Assert.True(first.Success); Assert.Equal(8, first.Nodes["distance"].Output<double>("Distance"), 10);
        var measured = first.Nodes["distance"].Output<MeasurementResult>("Result");
        var displaySpec = new NodeSpecification("report", "ResultDisplay", "Result", JsonDefaults.Element(new ResultDisplayParameters()));
        using var displayed = await executor.ExecuteSingleAsync(displaySpec, new Dictionary<string, object> { ["Measurement"] = measured });
        var report = displayed.Nodes["report"].Output<InspectionReport>("Result");
        Assert.True(report.IsOk); Assert.Equal(8, report.Measurement!.Values["Distance"]); Assert.Equal("px", report.Measurement.Unit);
        // Explicit existing decision retains authority: adding measurement must not rewrite old semantics.
        using var explicitDecision = await executor.ExecuteSingleAsync(displaySpec, new Dictionary<string, object> { ["Measurement"] = measured, ["IsOk"] = false });
        Assert.False(explicitDecision.Nodes["report"].Output<bool>("IsOk"));
        using var bad = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["LineA"] = new LineGeometry(new(0, 0), new(0, 0)) });
        Assert.True(bad.Success); Assert.False(bad.Nodes["distance"].Output<bool>("Valid"));
        Assert.Equal(0, bad.Nodes["distance"].Output<double>("Distance"));
        using var invalidReport = await executor.ExecuteSingleAsync(displaySpec, new Dictionary<string, object> { ["Measurement"] = bad.Nodes["distance"].Output<MeasurementResult>("Result") });
        Assert.False(invalidReport.Nodes["report"].Output<bool>("IsOk"));
    }
    [Fact]
    public async Task ParameterOnlyMeasurementNodesRunInOrdinaryDag()
    {
        var nodes = new[] { new NodeSpecification("distance", "DistanceMeasure", "Distance", JsonDefaults.Element(new DistanceMeasureOptions())),
            new("angle", "AngleMeasure", "Angle", JsonDefaults.Element(new AngleMeasureOptions())) };
        using var result = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(new NodeGraph(nodes, []));
        Assert.True(result.Success); Assert.Equal(10, result.Nodes["distance"].Output<double>("Distance")); Assert.Equal(90, result.Nodes["angle"].Output<double>("Angle"));
    }
}

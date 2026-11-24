using VisionPlatform.Algorithms.Measurement;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase4AMeasurementSessionTests
{
    [Fact]
    public async Task ConfigurationPreviewDoesNotLoadImagePixelsAndRunsThroughExistingRuntimeOnDemand()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionStudio-DeferredPreview-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var before = FrameHandle.ActiveBytes;
        try
        {
            using (var image = new ImageFrameBuilder(16, 16, PixelFormat.Mono8).Build()) ImageFile.Save(image, Path.Combine(directory, "input.png"));
            var project = new ProjectDocument { Nodes = [new("input", "ImageInput", "input", JsonDefaults.Element(new { Path = "input.png" })),
                new("display", "ImageDisplay", "display", JsonDefaults.Element(new { }))], Connections = [new("input", "Image", "display", "Image")] };
            var path = Path.Combine(directory, "project.json"); await new ProjectStore(BuiltinRegistration.CreateRegistry()).SaveAsync(path, project);
            using (var session = new EditorSession())
            {
                await session.OpenConfigurationAsync(path);
                Assert.Null(session.DisplayImage); Assert.Equal(before, FrameHandle.ActiveBytes); Assert.Empty(session.CurrentOutputs); Assert.Equal(2, session.Nodes.Count);
                await session.RunAsync(); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
                Assert.NotNull(session.DisplayImage); Assert.Equal(16, session.DisplayImage!.Width);
            }
            Assert.Equal(before, FrameHandle.ActiveBytes);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("VisionStudio-DeferredPreview-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(resolved, true);
        }
    }

    [Fact]
    public async Task CurrentReadOnlyOutputsAndMeasurementOverlaysExpireOnEditAndProjectChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionStudio-MeasurementSession-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var before = FrameHandle.ActiveBytes;
        try
        {
            using (var builder = new ImageFrameBuilder(160, 160, PixelFormat.Mono8))
            {
                for (var y = 0; y < 160; y++) for (var x = 0; x < 160; x++) builder.GetRowSpan(y)[x] = x < 80 ? (byte)20 : (byte)230;
                using var image = builder.Build(); ImageFile.Save(image, Path.Combine(directory, "edge.png"));
            }
            var project = new ProjectDocument
            {
                Nodes = [new("input", "ImageInput", "Input", JsonDefaults.Element(new ImageInputParameters { Path = "edge.png" })),
                    new("caliper", "Caliper", "Measure", JsonDefaults.Element(new CaliperOptions { CenterX = 80, CenterY = 80 })),
                    new("report", "ResultDisplay", "Report", JsonDefaults.Element(new ResultDisplayParameters()))],
                Connections = [new("input", "Image", "caliper", "Image"), new("caliper", "Result", "report", "Measurement")]
            };
            await new ProjectStore(BuiltinRegistration.CreateRegistry()).SaveAsync(Path.Combine(directory, "project.json"), project);
            using var session = new EditorSession(); var changes = 0;
            session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IEditorSession.CurrentOutputs)) changes++; };
            await session.OpenAsync(Path.Combine(directory, "project.json")); await session.RunAsync();
            Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
            Assert.Equal(3, session.CurrentOutputs.Count); Assert.NotEmpty(session.MeasurementOverlays);
            Assert.All(session.MeasurementOverlays, overlay => Assert.Equal("image", overlay.CoordinateSpaceId));
            var outputs = Assert.IsAssignableFrom<IDictionary<string, IReadOnlyDictionary<string, object>>>(session.CurrentOutputs);
            Assert.Throws<NotSupportedException>(() => outputs.Add("fake", new Dictionary<string, object>()));
            var report = (InspectionReport)session.CurrentOutputs["report"]["Result"];
            Assert.True(report.IsOk); Assert.True(report.Measurement!.Valid);
            Assert.True(session.InspectOutput("caliper", "Result"));
            Assert.Contains(session.SelectedOutputInspection!.Fields, f => f.Name == "测量类型" && f.Value == "Caliper");
            var caliper = session.Nodes.Single(n => n.NodeId == "caliper");
            session.CommitParameters(caliper, JsonDefaults.Element(new CaliperOptions { CenterX = 10, CenterY = 80, SearchLength = 10 }));
            Assert.Empty(session.CurrentOutputs); Assert.Empty(session.MeasurementOverlays); Assert.Null(session.SelectedOutputInspection);
            await session.RunAsync();
            Assert.False(((MeasurementResult)session.CurrentOutputs["caliper"]["Result"]).Valid); Assert.Empty(session.MeasurementOverlays);
            Assert.False(((InspectionReport)session.CurrentOutputs["report"]["Result"]).IsOk);
            await session.NewAsync(); Assert.Empty(session.CurrentOutputs); Assert.Empty(session.MeasurementOverlays);
            Assert.True(changes >= 4);
        }
        finally
        {
            var target = Path.GetFullPath(directory); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("VisionStudio-MeasurementSession-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
            Directory.Delete(target, true);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }
    [Fact]
    public void MeasurementInspectorBoundsLargePointSetsWithoutChangingValues()
    {
        var points = new EdgePointSet(Array.AsReadOnly(Enumerable.Range(0, 1000).Select(i => new VisionPlatform.Contracts.Geometry.Point2D(i, i)).ToArray()));
        var details = OutputInspector.Describe(new("node", "Points", "points", points));
        Assert.InRange(details.Fields.Count, 100, 105); Assert.Equal(1000, points.Points.Count);
        Assert.Contains(details.Fields, f => f.Name == "显示限制");
        Assert.Equal("几何测量", OutputInspector.Category(points));
    }
}

using System.Text.Json;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase4BIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase4B-Test-" + Guid.NewGuid().ToString("N"));
    private async Task<ExampleCatalog> Catalog()
    {
        await Phase4BExampleFactory.SaveAllAsync(_root);
        return new(_root, new ProjectStore(BuiltinRegistration.CreateRegistry()));
    }
    [Theory]
    [InlineData("ocr")]
    [InlineData("qr-code")]
    [InlineData("barcode")]
    public async Task RealOfflineExamplesRoundTripAndBlankFramesClearTextAndOverlays(string id)
    {
        var baseline = FrameHandle.ActiveBytes; var catalog = await Catalog();
        var entry = (await catalog.LoadAsync()).Single(e => e.Manifest.Id == id);
        Assert.Empty(await catalog.ValidateAsync(entry));
        using (var session = new EditorSession())
        {
            await session.OpenAsync(entry.ProjectPath); await session.RunAsync();
            Assert.True(ExampleVerifier.Verify(entry.Manifest.Expected, session.CurrentOutputs, session.Results.ToDictionary(r => r.NodeId, r => r.Status)).Passed);
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "recognize");
            Assert.NotEmpty(session.RecognitionOverlays);
            var output = session.SelectedOutputs.Single(o => o.PortName == "Result"); session.SelectOutput(output);
            Assert.Contains(session.SelectedOutputInspection!.Fields, f => f.Name == "成功" && f.Value == "True");
            var original = session.CurrentOutputs["recognize"]["Text"];
            await session.SaveAsync(); await session.OpenAsync(entry.ProjectPath); await session.RunAsync();
            Assert.Equal(original, session.CurrentOutputs["recognize"]["Text"]);
            var input = session.Nodes.Single(n => n.NodeId == "input");
            session.CommitParameters(input, JsonDefaults.Element(new ImageInputParameters { Path = "images/blank.png" }));
            Assert.Empty(session.CurrentOutputs); Assert.Empty(session.RecognitionOverlays); Assert.Null(session.SelectedOutputInspection);
            await session.RunAsync(); Assert.False((bool)session.CurrentOutputs["recognize"]["Success"]);
            Assert.Equal("", session.CurrentOutputs["text"]["Text"]); Assert.Empty(session.RecognitionOverlays);
            await session.NewAsync(); Assert.Empty(session.CurrentOutputs); Assert.Empty(session.RecognitionOverlays);
            await session.CloseAsync();
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }
    [Theory]
    [InlineData("ocr")]
    [InlineData("qr-code")]
    [InlineData("barcode")]
    public async Task DrawnRecognitionRoiUsesFormalParametersAndPreservesSchema(string id)
    {
        var catalog = await Catalog(); var entry = (await catalog.LoadAsync()).Single(e => e.Manifest.Id == id);
        using var session = new EditorSession(); await session.OpenAsync(entry.ProjectPath); await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.NodeId == "recognize");
        var frame = session.DisplayImage!; var roi = new RotatedRectRoi(frame.Width / 2d, frame.Height / 2d, frame.Width, frame.Height);
        session.UpdateRoi(roi); session.BindRoiToSelectedNode();
        Assert.True(session.SelectedNode.Parameters.GetProperty("UseRoi").GetBoolean());
        Assert.Equal(frame.Width, session.SelectedNode.Parameters.GetProperty("RoiWidth").GetDouble());
        await session.SaveAsync(); await session.OpenAsync(entry.ProjectPath); await session.RunAsync();
        Assert.True((bool)session.CurrentOutputs["recognize"]["Success"]);
        Assert.Equal(ProjectDocument.CurrentSchemaVersion, session.SnapshotProject().SchemaVersion); Assert.Single(session.SnapshotProject().ROI);
    }
    [Fact]
    public async Task YoloExampleIsHonestAboutMissingExternalResourcesAndNeverReturnsFakeBoxes()
    {
        var catalog = await Catalog(); var entry = (await catalog.LoadAsync()).Single(e => e.Manifest.Id == "yolo-external");
        Assert.NotEmpty(entry.Manifest.ExternalDependencies); Assert.Empty(await catalog.ValidateAsync(entry));
        var project = await new ProjectStore(BuiltinRegistration.CreateRegistry()).LoadAsync(entry.ProjectPath);
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(project.ToGraph(), entry.Directory);
        Assert.False(run.Success); Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["recognize"].Status);
        Assert.Contains("Python", run.Nodes["recognize"].ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(run.Nodes["recognize"].Outputs);
    }
    [Fact]
    public void StructuredRecognitionInspectorDoesNotFallBackToRawObjectStrings()
    {
        var result = new YoloInferenceResult(true, [new(2, "part", .91, new(10, 20, 30, 40))], "image", TimeSpan.FromMilliseconds(5), [], "abc", "CPU", 2, 1);
        var inspection = OutputInspector.Describe(new("n", "Result", "YOLO", result));
        Assert.Equal("AI 检测", OutputInspector.Category(result)); Assert.Contains(inspection.Fields, f => f.Name == "目标数" && f.Value == "1");
        Assert.Contains(inspection.Fields, f => f.Name == "模型 SHA256" && f.Value == "abc");
    }
    public void Dispose()
    {
        if (Directory.Exists(_root) && Path.GetFileName(_root).StartsWith("VisionStudio-Phase4B-Test-", StringComparison.Ordinal)) Directory.Delete(_root, true);
    }
}

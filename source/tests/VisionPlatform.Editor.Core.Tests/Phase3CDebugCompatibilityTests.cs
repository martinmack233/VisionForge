using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase3CDebugCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase3CDebug-" + Guid.NewGuid().ToString("N"));
    private ProjectStore Store()
    {
        var matcher = new CvInspectTemplateMatcher();
        return new(BuiltinRegistration.CreateRegistry(), new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
    }
    private async Task Create() => await Phase3CSampleFactory.SaveAllAsync(_root, false);

    [Fact]
    public void LargeTextDetailsAreBoundedWithoutChangingTheActualOutput()
    {
        var message = new string('x', 20000); var row = new OutputValueRow("text", "Text", "长文本", message);
        var details = OutputInspector.Describe(row);
        Assert.InRange(Assert.Single(details.Fields).Value.Length, 16384, 17000);
        Assert.Contains("共20000字符", details.Fields[0].Value); Assert.Same(message, row.Value);
        Assert.Equal(20000, ((string)row.Value).Length);
    }

    [Fact]
    public async Task ImageAndNumberPortsInspectCurrentValuesAndEditsClearDetails()
    {
        await Create(); var before = FrameHandle.ActiveBytes;
        using var session = new EditorSession(); await session.OpenAsync(Path.Combine(_root, "flow1-preprocessing.json"));
        Assert.False(session.InspectOutput("otsu", "CalculatedThreshold")); Assert.Null(session.SelectedOutputInspection);
        await session.RunAsync(); Assert.True(session.InspectOutput("otsu", "CalculatedThreshold"));
        var actual = (double)session.SelectedOutputs.Single(o => o.PortName == "CalculatedThreshold").Value;
        var details = session.SelectedOutputInspection!;
        Assert.Equal("Double", details.DataType); Assert.Equal("CalculatedThreshold", details.PortName);
        Assert.Equal(actual.ToString("G12", System.Globalization.CultureInfo.InvariantCulture), Assert.Single(details.Fields).Value);
        Assert.Equal("数值", session.SelectedOutputs.Single(o => o.PortName == "CalculatedThreshold").Category);
        Assert.True(session.InspectOutput("otsu", "BinaryImage"));
        Assert.Equal("图像", session.SelectedOutputs.Single(o => o.PortName == "BinaryImage").Category);
        Assert.Equal(200, session.DisplayImage!.Width); Assert.Equal(144, session.DisplayImage.Height);
        Assert.Contains(session.SelectedOutputInspection!.Fields, f => f.Name == "像素格式" && f.Value == "Mono8");
        session.CommitParameters(session.SelectedNode!, JsonDefaults.Element(new { MaxValue = 128 }));
        Assert.Null(session.SelectedOutputInspection); Assert.Empty(session.SelectedOutputs);
        Assert.False(session.InspectOutput("otsu", "BinaryImage"));
        await session.RunAsync(EditorRunMode.Single); Assert.True(session.InspectOutput("otsu", "BinaryImage"));
        await session.CloseAsync(); Assert.Null(session.SelectedOutputInspection); Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task BooleanAndGeometryUseTypedFieldsAndSwitchClearsSelection()
    {
        await Create(); using var session = new EditorSession();
        await session.OpenAsync(Path.Combine(_root, "flow2-filter.json")); await session.RunAsync();
        Assert.True(session.InspectOutput("report", "IsOk")); Assert.Equal("True", Assert.Single(session.SelectedOutputInspection!.Fields).Value);
        Assert.Equal("Boolean", session.SelectedOutputs.Single(o => o.PortName == "IsOk").Category);
        await session.OpenAsync(Path.Combine(_root, "flow6-enhance-transform.json")); Assert.Null(session.SelectedOutputInspection);
        await session.RunAsync(); Assert.All(session.Results, row => Assert.Equal("Success", row.Status));
        Assert.True(session.InspectOutput("rotate", "CoordinateTransform"));
        Assert.Equal("CoordinateTransform2D", session.SelectedOutputInspection!.DataType);
        Assert.Contains(session.SelectedOutputInspection.Fields, f => f.Name == "M11");
        Assert.True(session.InspectOutput("rotate", "RotatedImage"));
        var expected = (ImageFrame)session.SelectedOutputs.Single(o => o.PortName == "RotatedImage").Value;
        Assert.Equal(expected.CoordinateSpace, session.DisplayImage!.CoordinateSpace);
        await session.NewAsync(); Assert.Null(session.SelectedOutputInspection); Assert.Empty(session.SelectedOutputs);
    }

    [Fact]
    public async Task NewAlgorithmParametersAndControlConnectionsRoundTripWithoutSchemaUpgrade()
    {
        await Create();
        foreach (var (name, original) in Phase3CSampleFactory.CreateProjects())
        {
            var loaded = await Store().LoadAsync(Path.Combine(_root, name));
            Assert.Equal(ProjectDocument.CurrentSchemaVersion, loaded.SchemaVersion); Assert.Equal(original.Connections, loaded.Connections);
            foreach (var (before, after) in original.Nodes.Zip(loaded.Nodes))
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before.Parameters.GetRawText()), JsonNode.Parse(after.Parameters.GetRawText())), "Saved parameter values changed: " + before.NodeId);
        }
    }

    [Theory]
    [InlineData("tests/Fixtures/legacy-editor-project.json")]
    [InlineData("samples/phase3a/flow1.json")]
    [InlineData("samples/phase3b/flow1.json")]
    public async Task PriorPhaseProjectDataReopensAndExecutesWithPreservedConnectionsAndRoi(string oldRelativePath)
    {
        await Create();
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "VisionPlatform.sln"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var original = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(Path.Combine(repository!.FullName, oldRelativePath)), JsonDefaults.Options)!;
        // Clone the old document and rebase only its external image reference to this test's fixture.
        var project = original with { Nodes = original.Nodes.Select(n => n.NodeTypeId == "ImageInput" ? n with
        { Parameters = JsonDefaults.Element(new ImageInputParameters { Path = "inspection.png", PixelFormat = PixelFormat.Mono8 }) } : n).ToList() };
        var copy = Path.Combine(_root, Path.GetFileName(Path.GetDirectoryName(oldRelativePath)) + "-legacy.json");
        await Store().SaveAsync(copy, project);
        using var session = new EditorSession(); await session.OpenAsync(copy); await session.RunAsync();
        Assert.All(session.Results, row => Assert.Contains(row.Status, new[] { "Success", "Skipped" }));
        Assert.DoesNotContain(session.Results, row => row.Status is "Failed" or "Cancelled");
        Assert.Equal(original.Connections, session.SnapshotProject().Connections); Assert.Equal(original.ROI, session.SnapshotProject().ROI);
        Assert.Equal(original.Nodes.Select(n => n.NodeTypeId), session.Nodes.Select(n => n.TypeId));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_root); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("VisionStudio-Phase3CDebug-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}

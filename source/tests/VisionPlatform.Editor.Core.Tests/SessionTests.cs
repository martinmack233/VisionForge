using System.Text.Json;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionPlatform.Editor.Core.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VisionStudio-Session-" + Guid.NewGuid().ToString("N"));
    public SessionTests() => DemoProjectFactory.CreateResources(_directory);
    private ProjectStore Store()
    {
        var matcher = new CvInspectTemplateMatcher();
        return new(BuiltinRegistration.CreateRegistry(), new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
    }
    private async Task<string> SaveFixture(bool preprocessing)
    {
        var path = Path.Combine(_directory, preprocessing ? "flow-b.json" : "flow-a.json");
        await Store().SaveAsync(path, DemoProjectFactory.Create(preprocessing)); return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorUsesSameRuntimeAndMeasurementsAsConsoleProject(bool preprocessing)
    {
        var path = await SaveFixture(preprocessing);
        using var session = new EditorSession(); await session.OpenAsync(path); await session.RunAsync();
        Assert.Equal(session.Nodes.Count, session.Results.Count);
        Assert.All(session.Results, row => Assert.Equal("Success", row.Status));
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "TemplateMatching");
        var actual = (TemplateMatchResult)session.SelectedOutputs.Single(o => o.PortName == "MatchResult").Value;
        using var expected = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync((await Store().LoadAsync(path)).ToGraph(), _directory);
        var match = expected.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
        Assert.Equal(match.Score, actual.Score); Assert.Equal(match.CenterX, actual.CenterX); Assert.Equal(match.CenterY, actual.CenterY);
        Assert.Equal(match.AngleDegrees, actual.AngleDegrees); Assert.Equal(match.Scale, actual.Scale);
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "BlobDetection");
        Assert.Equal(2, session.SelectedOutputs.Single(o => o.PortName == "BlobCount").Value);
        Assert.Contains(session.Overlays, o => o.Kind == OverlayKind.FollowedRoi && o.IsValid);
    }

    [Fact]
    public async Task EditingImmediatelyInvalidatesOutputsAndImportingAbsentImageNeverReusesPose()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true)); await session.RunAsync();
        var blur = session.Nodes.Single(n => n.TypeId == "GaussianBlur"); var revision = session.Revision;
        session.CommitParameters(blur, blur.Parameters);
        Assert.Equal(revision + 1, session.Revision); Assert.Empty(session.Results); Assert.Empty(session.SelectedOutputs);
        Assert.DoesNotContain(session.Overlays, o => o.Kind == OverlayKind.FollowedRoi);
        await session.ImportImageAsync(Path.Combine(_directory, "absent.png")); await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "TemplateMatching");
        Assert.False((bool)session.SelectedOutputs.Single(o => o.PortName == "Success").Value);
        Assert.DoesNotContain(session.SelectedOutputs, o => o.PortName == "Pose2D");
        Assert.DoesNotContain(session.Overlays, o => o.Kind == OverlayKind.FollowedRoi && o.IsValid);
    }

    [Fact]
    public async Task ConnectionsRejectWrongTypeCycleAndDuplicateButAllowIncompleteAuthoringGraph()
    {
        using var session = new EditorSession(); await session.NewAsync(); await session.ImportImageAsync(Path.Combine(_directory, "sample.png"));
        var image = session.Nodes.Single(); session.AddNode("GaussianBlur"); var blur = session.SelectedNode!;
        session.AddNode("ResultDecision"); var decision = session.SelectedNode!;
        Assert.True(session.TryConnect(image.Outputs.Single(), blur.Inputs.Single(p => p.Name == "Image"), out _));
        Assert.False(session.TryConnect(image.Outputs.Single(), decision.Inputs.Single(p => p.Name == "BlobCount"), out var wrongType));
        Assert.Contains("mismatch", wrongType);
        Assert.False(session.TryConnect(image.Outputs.Single(), blur.Inputs.Single(p => p.Name == "Image"), out var duplicate));
        Assert.Contains("multiple", duplicate);
        session.Disconnect(session.Connections.Single());
        Assert.False(session.TryConnect(blur.Outputs.Single(), blur.Inputs.Single(p => p.Name == "Image"), out var cycle));
        Assert.Contains("cycle", cycle);
    }

    [Fact]
    public async Task SingleNodeRunsFreshDependenciesEvenWhenUnrelatedNodeIsIncomplete()
    {
        using var session = new EditorSession(); await session.ImportImageAsync(Path.Combine(_directory, "sample.png"));
        var input = session.Nodes.Single(); session.AddNode("GaussianBlur"); var blur = session.SelectedNode!;
        Assert.True(session.TryConnect(input.Outputs.Single(), blur.Inputs.First(), out _));
        session.AddNode("BlobDetection"); session.SelectedNode = blur;
        await session.RunAsync(EditorRunMode.Single);
        Assert.Single(session.Results); Assert.Equal(blur.NodeId, session.Results[0].NodeId); Assert.Equal("Success", session.Results[0].Status);
        var processed = (ImageFrame)session.SelectedOutputs.Single(o => o.PortName == "ProcessedImage").Value;
        Assert.False(processed.IsDisposed); Assert.Equal(384, processed.Width);
    }

    [Fact]
    public async Task SaveAsKeepsReferencesAndSeparatesFormalRoiFromLayout()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true));
        var roiNode = session.Nodes.Single(n => n.TypeId == "ROITransform"); session.SelectedNode = roiNode;
        var roi = new RotatedRectRoi(165, 100, 46, 30, 12);
        session.UpdateRoi(roi); session.MoveNode(roiNode, 432.5, 112.25);
        var revision = session.Revision; session.MoveNode(roiNode, 456.5, 123.25); Assert.Equal(revision, session.Revision);
        var saved = Path.Combine(_directory, "relocated", "inspection.json"); await session.SaveAsync(saved);
        var formal = await Store().LoadAsync(saved); Assert.Equal(ProjectDocument.CurrentSchemaVersion, formal.SchemaVersion); Assert.Equal(roi, formal.ROI[0]);
        Assert.Equal(roi, formal.Nodes.Single(n => n.NodeTypeId == "ROITransform").Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!.ReferenceROI);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(saved)); Assert.False(json.RootElement.TryGetProperty("Layout", out _));
        Assert.True(File.Exists(saved + ".editor.json"));
        using var reopened = new EditorSession(); await reopened.OpenAsync(saved);
        var restored = reopened.Nodes.Single(n => n.TypeId == "ROITransform"); Assert.Equal(456.5, restored.X); Assert.Equal(123.25, restored.Y);
        await reopened.RunAsync(); Assert.All(reopened.Results, r => Assert.Equal("Success", r.Status));
    }

    [Fact]
    public async Task ImageBudgetRetainsOnlyNumericResultsAndCloseReleasesAllPlatformImages()
    {
        var before = FrameHandle.ActiveBytes;
        using (var session = new EditorSession(BuiltinRegistration.CreateRegistry(), 1))
        {
            await session.OpenAsync(await SaveFixture(true)); await session.RunAsync();
            Assert.Contains("缓存上限", session.StatusMessage);
            foreach (var node in session.Nodes)
            { session.SelectedNode = node; Assert.DoesNotContain(session.SelectedOutputs, o => o.Value is ImageFrame); }
            Assert.Equal(384L * 320 * 3, session.RetainedImageBytes);
            await session.CloseAsync(); Assert.Null(session.DisplayImage);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task RepeatedRunAndProjectSwitchReleasePriorImages()
    {
        var before = FrameHandle.ActiveBytes;
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true));
        long? stable = null;
        for (var i = 0; i < 5; i++) { await session.RunAsync(); stable ??= FrameHandle.ActiveBytes; Assert.Equal(stable, FrameHandle.ActiveBytes); }
        await session.NewAsync(); Assert.Equal(before, FrameHandle.ActiveBytes); Assert.Empty(session.Results);
        await session.OpenAsync(await SaveFixture(false)); await session.RunAsync(); await session.CloseAsync();
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task EditsCancelRunningSnapshotAndCloseDrainsBeforeRelease()
    {
        var gate = new GateState(); var registry = BuiltinRegistration.CreateRegistry(); registry.Register("Gate", () => new GateNode(gate));
        using var session = new EditorSession(registry); session.Catalog.Register<EmptyParameters>("Gate", "Gate", "Test");
        await session.ImportImageAsync(Path.Combine(_directory, "sample.png")); var input = session.Nodes.Single();
        session.AddNode("Gate"); var slow = session.SelectedNode!; Assert.True(session.TryConnect(input.Outputs.Single(), slow.Inputs.Single(), out _));
        var run = session.RunAsync(); await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revision = session.Revision; session.CommitParameters(input, input.Parameters); Assert.Equal(revision + 1, session.Revision);
        var close = session.CloseAsync(); Assert.False(close.IsCompleted); gate.Release.TrySetResult();
        await run; await close; Assert.Empty(session.Results); Assert.Null(session.DisplayImage); Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task SingleNodeBorrowedOutputKeepsFreshInputLeaseAlive()
    {
        var registry = BuiltinRegistration.CreateRegistry(); registry.Register("Borrow", () => new BorrowNode());
        using var session = new EditorSession(registry); session.Catalog.Register<EmptyParameters>("Borrow", "Borrow", "Test");
        await session.ImportImageAsync(Path.Combine(_directory, "sample.png")); var input = session.Nodes.Single();
        session.AddNode("Borrow"); var node = session.SelectedNode!; Assert.True(session.TryConnect(input.Outputs.Single(), node.Inputs.Single(), out _));
        await session.RunAsync(EditorRunMode.Single);
        var image = (ImageFrame)session.SelectedOutputs.Single().Value; Assert.False(image.IsDisposed);
        using (var pixels = image.Lease()) Assert.Equal(35, pixels.GetRowSpan(0)[0]);
        session.CommitParameters(node, node.Parameters); Assert.True(image.IsDisposed);
    }

    [Fact]
    public async Task ProjectSwitchCancelsQueuedRunsBeforeTheyStart()
    {
        var gate = new GateState(); var registry = BuiltinRegistration.CreateRegistry(); registry.Register("Gate", () => new GateNode(gate));
        using var session = new EditorSession(registry); session.Catalog.Register<EmptyParameters>("Gate", "Gate", "Test");
        await session.ImportImageAsync(Path.Combine(_directory, "sample.png")); var input = session.Nodes.Single();
        session.AddNode("Gate"); Assert.True(session.TryConnect(input.Outputs.Single(), session.SelectedNode!.Inputs.Single(), out _));
        var first = session.RunAsync(); await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = session.RunAsync(); var switching = session.NewAsync(); gate.Release.TrySetResult();
        await Task.WhenAll(first, queued, switching);
        Assert.Equal(1, gate.Invocations); Assert.Empty(session.Nodes); Assert.Empty(session.Results); Assert.Null(session.DisplayImage);
    }

    [Fact]
    public async Task TemplateTeachingRunsCurrentPreprocessingDomainAndPersistsResource()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true));
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "TemplateMatching");
        var roi = new RotatedRectRoi(221, 163, 42, 36);
        session.UpdateRoi(roi); await session.TeachTemplateAsync();
        var p = session.SelectedNode!.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!;
        using var template = TemplateResourceStore.Load(p.TemplatePath);
        using var expected = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteToAsync(session.SnapshotProject().ToGraph(), "blur", _directory);
        using var crop = expected.Nodes["blur"].Output<ImageFrame>("ProcessedImage").CreateView(200, 145, 42, 36);
        using var actualPixels = template.Image.Lease(); using var expectedPixels = crop.Lease();
        for (var row = 0; row < crop.Height; row++) Assert.True(actualPixels.GetRowSpan(row).SequenceEqual(expectedPixels.GetRowSpan(row)));
        Assert.Contains(session.SnapshotProject().TemplateResources, r => Path.GetFullPath(r.Path) == Path.GetFullPath(p.TemplatePath));
    }

    [Fact]
    public async Task BindingRoiCapturesCurrentReferencePoseAndInvalidatesOldResults()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(false));
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ROITransform");
        session.UpdateRoi(new(290, 175, 42, 28, 10)); await session.RunAsync();
        var expectedPose = session.Overlays.Single(o => o.Kind == OverlayKind.Match).Region;
        session.BindRoiToSelectedNode();
        var p = session.SelectedNode!.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!;
        Assert.Equal(expectedPose.CenterX, p.ReferencePose.CenterX); Assert.Equal(expectedPose.CenterY, p.ReferencePose.CenterY);
        Assert.Empty(session.Results); Assert.DoesNotContain(session.Overlays, o => o.Kind == OverlayKind.FollowedRoi);
    }

    [Fact]
    public async Task DrawAfterSuccessfulMatchThenSelectAndBindRetainsCorrectTeachingPose()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(false)); await session.RunAsync();
        var match = session.Overlays.Single(o => o.Kind == OverlayKind.Match).Region;
        var followed = session.Overlays.Single(o => o.Kind == OverlayKind.FollowedRoi).Region;
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ImageInput");
        session.UpdateRoi(followed);
        Assert.Empty(session.Results);
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ROITransform");
        Assert.Equal(followed, session.EditedRoi); // Selecting the target must not discard the pending drawn region.
        session.BindRoiToSelectedNode();
        var parameters = session.SelectedNode.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!;
        Assert.Equal(match.CenterX, parameters.ReferencePose.CenterX); Assert.Equal(match.AngleDegrees, parameters.ReferencePose.AngleDegrees);
        await session.RunAsync();
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "BlobDetection");
        Assert.Equal(2, session.SelectedOutputs.Single(o => o.PortName == "BlobCount").Value);
        Assert.All(session.Results, result => Assert.Equal("Success", result.Status));
    }

    [Fact]
    public async Task ImportAndFailedMatchCannotReusePendingTeachingPose()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(false)); await session.RunAsync();
        session.UpdateRoi(session.Overlays.Single(o => o.Kind == OverlayKind.FollowedRoi).Region);
        await session.ImportImageAsync(Path.Combine(_directory, "absent.png"));
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ROITransform");
        Assert.Throws<InvalidOperationException>(session.BindRoiToSelectedNode);
        await session.RunAsync();
        session.UpdateRoi(new(150, 100, 30, 20));
        Assert.Throws<InvalidOperationException>(session.BindRoiToSelectedNode);
    }

    [Fact]
    public async Task ParameterEditedReferenceRoiAlsoUpdatesFormalRoiList()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(false));
        var node = session.Nodes.Single(n => n.TypeId == "ROITransform");
        var p = node.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!;
        var edited = new RotatedRectRoi(160, 110, 44, 28, 8);
        session.CommitParameters(node, JsonDefaults.Element(p with { ReferenceROI = edited }));
        Assert.Equal(edited, session.SnapshotProject().ROI[0]);
        Assert.Equal(edited, session.Overlays.Single(o => o.Kind == OverlayKind.ReferenceRoi).Region);
    }

    [Fact]
    public async Task ImageParameterChangeClearsOldPreviewAndRuntimeAdoptsActualInput()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true));
        Assert.Equal(PixelFormat.BGR8, session.DisplayImage!.PixelFormat);
        var input = session.Nodes.Single(n => n.TypeId == "ImageInput");
        session.CommitParameters(input, JsonDefaults.Element(new ImageInputParameters { Path = Path.Combine(_directory, "shifted.png") }));
        Assert.Null(session.DisplayImage);
        await session.RunAsync();
        session.SelectedNode = input; var actual = (ImageFrame)session.SelectedOutputs.Single(o => o.PortName == "Image").Value;
        Assert.Same(actual.BufferHandle, session.DisplayImage!.BufferHandle); Assert.Equal(PixelFormat.Mono8, session.DisplayImage.PixelFormat);
        var blur = session.Nodes.Single(n => n.TypeId == "GaussianBlur"); session.CommitParameters(blur, blur.Parameters);
        Assert.NotNull(session.DisplayImage); Assert.Equal(PixelFormat.Mono8, session.DisplayImage.PixelFormat);
        Assert.Equal(384L * 320, session.RetainedImageBytes);
    }

    [Fact]
    public async Task FailedOpenPreservesPreviousProjectAndImage()
    {
        using var session = new EditorSession(); await session.OpenAsync(await SaveFixture(true)); var id = session.SnapshotProject().ProjectId;
        await Assert.ThrowsAsync<FileNotFoundException>(() => session.OpenAsync(Path.Combine(_directory, "missing.json")));
        Assert.Equal(id, session.SnapshotProject().ProjectId); Assert.NotNull(session.DisplayImage); Assert.False(session.DisplayImage.IsDisposed);
    }

    public sealed class EmptyParameters;
    private sealed class GateState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Invocations;
    }
    private sealed class GateNode(GateState state) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new("Gate", "1.0.0", [new("Image", typeof(ImageFrame))], [new("Image", typeof(ImageFrame))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken ct)
        { Interlocked.Increment(ref state.Invocations); state.Started.TrySetResult(); await state.Release.Task; ct.ThrowIfCancellationRequested(); return new(new Dictionary<string, object> { ["Image"] = context.Input<ImageFrame>("Image") }); }
    }
    private sealed class BorrowNode : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new("Borrow", "1.0.0", [new("Image", typeof(ImageFrame))], [new("Image", typeof(ImageFrame))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken ct) => ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Image"] = context.Input<ImageFrame>("Image") }));
    }
    public void Dispose()
    {
        var path = Path.GetFullPath(_directory); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("VisionStudio-Session-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path");
        Directory.Delete(path, true);
    }
}

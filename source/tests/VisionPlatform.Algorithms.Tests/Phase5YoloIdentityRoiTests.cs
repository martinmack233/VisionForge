using System.Text.Json;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase5YoloIdentityRoiTests
{
    private static NodeSpecification RoiSpec(YoloDetectionRoiOptions? p = null) => new("roi", "YoloDetectionRoi", "target", JsonDefaults.Element(p ?? new()));
    private static YoloInferenceResult Result(bool empty = false) => new(true,
        empty ? [] : [new(1, "scratch", .9, new(2, 4, 8, 6)), new(2, "bubble", .4, new(1, 1, 2, 2))], "crop", TimeSpan.Zero, [])
        { FrameId = Guid.NewGuid(), TaskId = "current-task" };
    private static RuntimeExecutor Executor() { var registry = new NodeRegistry(); registry.Register(() => new YoloDetectionRoiNode()); return new(registry); }

    [Fact]
    public async Task ExplicitCropTransformMapsCurrentTargetToExistingOriginalRoi()
    {
        var transform = new CoordinateTransform2D(0, -2, 2, 0, 100, 200, "crop", "sensor");
        using var run = await Executor().ExecuteSingleAsync(RoiSpec(new() { ClassIds = "1", MinimumConfidence = .8 }),
            new Dictionary<string, object> { ["Result"] = Result(), ["CoordinateTransform"] = transform });
        Assert.True(run.Success); var result = run.Nodes["roi"];
        var roi = result.Output<RotatedRectRoi>("ROI");
        Assert.Equal("sensor", roi.CoordinateSpaceId); Assert.Equal(86, roi.CenterX, 8); Assert.Equal(212, roi.CenterY, 8);
        Assert.Equal(16, roi.Width, 8); Assert.Equal(12, roi.Height, 8); Assert.Equal(90, roi.AngleDegrees, 8);
        Assert.Equal(1, result.Output<int>("Count")); Assert.Equal("scratch", result.Output<string>("Label"));
    }

    [Fact]
    public async Task EmptyCurrentDetectionsNeverPublishOldRoiOrRunWholeImageFallback()
    {
        var registry = new NodeRegistry(); registry.Register(() => new YoloDetectionRoiNode()); registry.Register(() => new BlobDetectionNode());
        registry.Register("source", () => new Source(Result(true)));
        var graph = new NodeGraph([new("source", "source", "source", JsonDefaults.Element(new { })), RoiSpec(),
            new("blob", "BlobDetection", "blob", JsonDefaults.Element(new VisionPlatform.Algorithms.Blob.BlobOptions()))],
            [new("source", "Result", "roi", "Result"), new("source", "Image", "blob", "Image"), new("roi", "ROI", "blob", "ROI")]);
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(graph);
        Assert.False(run.Nodes["roi"].Output<bool>("Found")); Assert.Equal(0, run.Nodes["roi"].Output<int>("Count"));
        Assert.False(run.Nodes["roi"].Outputs.ContainsKey("ROI"));
        Assert.Equal(NodeExecutionStatus.Skipped, run.Nodes["blob"].Status); Assert.Empty(run.Nodes["blob"].Outputs);
    }

    [Fact]
    public async Task WrongSpaceAndShearAreExplicitFailuresUnlessEnvelopeWasSelected()
    {
        var wrong = new CoordinateTransform2D(1, 0, 0, 1, 0, 0, "different", "image");
        using var badSpace = await Executor().ExecuteSingleAsync(RoiSpec(), new Dictionary<string, object> { ["Result"] = Result(), ["CoordinateTransform"] = wrong });
        Assert.Equal(NodeExecutionStatus.Failed, badSpace.Nodes["roi"].Status); Assert.Empty(badSpace.Nodes["roi"].Outputs);
        var shear = new CoordinateTransform2D(1, .5, 0, 1, 0, 0, "crop", "image");
        using var rejected = await Executor().ExecuteSingleAsync(RoiSpec(), new Dictionary<string, object> { ["Result"] = Result(), ["CoordinateTransform"] = shear });
        Assert.Equal(NodeExecutionStatus.Failed, rejected.Nodes["roi"].Status);
        using var selected = await Executor().ExecuteSingleAsync(RoiSpec(new() { AxisAlignedEnvelope = true }), new Dictionary<string, object> { ["Result"] = Result(), ["CoordinateTransform"] = shear });
        Assert.True(selected.Success); Assert.Equal(0, selected.Nodes["roi"].Output<RotatedRectRoi>("ROI").AngleDegrees);
    }

    [Fact]
    public async Task NodeBindsFrameAndRuntimeIdentityWithoutChangingInferencePortSemantics()
    {
        using var image = FrameFactory.CopyFrom(new byte[64], 8, 8, PixelFormat.Mono8);
        var registry = new NodeRegistry(); registry.Register(() => new YoloInferenceNode(new Inference()));
        var spec = new NodeSpecification("yolo", "YoloInference", "yolo", JsonDefaults.Element(new YoloInferenceOptions { AIProjectId = "ai-p", AIWorkspaceId = "ai-w", ModelRecordId = "m" }));
        using var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image });
        var result = run.Nodes["yolo"].Output<YoloInferenceResult>("Result");
        Assert.Equal(image.FrameId, result.FrameId); Assert.Equal(run.RunId, result.RunId); Assert.Equal("ai-p", result.AIProjectId);
        Assert.Equal("ai-w", result.AIWorkspaceId); Assert.Equal("m", result.ModelRecordId); Assert.NotEmpty(result.TaskId);
        Assert.Equal(0, run.Nodes["yolo"].Output<int>("Count")); Assert.True(run.Nodes["yolo"].Output<bool>("Success"));
    }
    private sealed class Inference : IYoloInference
    { public ValueTask<YoloInferenceResult> InferAsync(ImageFrame image, YoloInferenceOptions p, RotatedRectRoi? roi = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(new YoloInferenceResult(true, [], image.CoordinateSpace.Id, TimeSpan.Zero, [])); }
    private sealed class Source(YoloInferenceResult result) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new("source", "1.0.0", [], [new("Result", typeof(YoloInferenceResult)), new("Image", typeof(ImageFrame))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
        { var frame = FrameFactory.CopyFrom(new byte[400], 20, 20, PixelFormat.Mono8); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = result, ["Image"] = frame }, [frame])); }
    }
}

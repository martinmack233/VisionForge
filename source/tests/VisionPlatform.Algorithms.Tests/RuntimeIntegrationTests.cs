using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

/// <summary>Real native algorithms connected only through the production Runtime.</summary>
public sealed class RuntimeIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-Integration-" + Guid.NewGuid().ToString("N"));
    private static readonly Pose2D ReferencePose = new(99.5, 89.5);
    private static readonly RotatedRectRoi ReferenceRoi = new(175.5, 89.5, 60, 50);

    public RuntimeIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        using var template = new Mat(40, 48, MatType.CV_8UC1);
        var random = new Random(20261030);
        var rows = template.Rows; var cols = template.Cols;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++) template.Set(y, x, (byte)random.Next(70, 150));
        Cv2.GaussianBlur(template, template, new(3, 3), .6);
        Cv2.Rectangle(template, new Rect(3, 3, 41, 33), Scalar.All(210), 2);
        Cv2.Circle(template, new Point(13, 13), 6, Scalar.All(30), -1);
        Cv2.PutText(template, "R", new(24, 29), HersheyFonts.HersheySimplex, .65, Scalar.All(245), 2);
        using var frame = FrameFactory.CopyFromMat(template, PixelFormat.Mono8);
        using var model = new TemplateModel(frame, "integration-fixture");
        TemplateResourceStore.Save(Path.Combine(_directory, "template.json"), model);
        using var scene = new Mat(240, 320, MatType.CV_8UC1, Scalar.All(45));
        using (var destination = new Mat(scene, new Rect(106, 130, template.Cols, template.Rows))) template.CopyTo(destination);
        Cv2.Circle(scene, new(194, 150), 5, Scalar.All(235), -1);
        Cv2.Circle(scene, new(218, 150), 6, Scalar.All(235), -1);
        Cv2.ImWrite(Path.Combine(_directory, "scene.png"), scene);
        using var blank = new Mat(240, 320, MatType.CV_8UC1, Scalar.All(45));
        Cv2.ImWrite(Path.Combine(_directory, "blank.png"), blank);
    }

    private static NodeSpecification Spec(string id, string type, object parameters) => new(id, type, id, JsonDefaults.Element(parameters));
    private static NodeConnection Edge(string source, string output, string target, string input) => new(source, output, target, input);
    private static TemplateMatchOptions MatchOptions => new()
    {
        MinimumScore = .7, AngleMinDegrees = 0, AngleMaxDegrees = 0,
        ScaleMin = 1, ScaleMax = 1, CoarseScale = 1
    };

    private static NodeGraph Flow(bool preprocessing, string imagePath = "scene.png")
    {
        var nodes = new List<NodeSpecification>
        {
            Spec("image", "ImageInput", new ImageInputParameters { Path = imagePath, PixelFormat = preprocessing ? PixelFormat.RGB8 : PixelFormat.Mono8 })
        };
        var connections = new List<NodeConnection>();
        var sourceNode = "image"; var sourcePort = "Image";
        if (preprocessing)
        {
            nodes.Add(Spec("convert", "PixelFormatConvert", new PixelFormatConvertParameters { TargetFormat = PixelFormat.BGR8 }));
            nodes.Add(Spec("gaussian", "GaussianBlur", new GaussianBlurParameters { Options = new() { KernelSize = 3, SigmaX = .7 } }));
            connections.Add(Edge("image", "Image", "convert", "Image"));
            connections.Add(Edge("convert", "ConvertedImage", "gaussian", "Image"));
            sourceNode = "gaussian"; sourcePort = "ProcessedImage";
        }
        nodes.Add(Spec("match", "TemplateMatching", new TemplateMatchingParameters { TemplatePath = "template.json", Options = MatchOptions }));
        nodes.Add(Spec("roi", "ROITransform", new ROITransformParameters { ReferencePose = ReferencePose, ReferenceROI = ReferenceRoi }));
        nodes.Add(Spec("blob", "BlobDetection", new BlobOptions { Threshold = 180, MinArea = 20, MaxArea = 300 }));
        nodes.Add(Spec("decision", "ResultDecision", new ResultDecisionParameters { MinimumBlobCount = 2, MaximumBlobCount = 2 }));
        connections.AddRange([
            Edge(sourceNode, sourcePort, "match", "Image"), Edge(sourceNode, sourcePort, "roi", "Image"),
            Edge("match", "Pose2D", "roi", "Pose2D"), Edge(sourceNode, sourcePort, "blob", "Image"),
            Edge("roi", "ROI", "blob", "ROI"), Edge("blob", "BlobCount", "decision", "BlobCount"),
            Edge("match", "Success", "decision", "MatchSuccess")
        ]);
        return new NodeGraph(nodes, connections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothRequiredFlowsRunRealAlgorithmsThroughTheSameRuntime(bool preprocessing)
    {
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(Flow(preprocessing), _directory);
        Assert.True(run.Success, run.ErrorMessage);
        var match = run.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
        Assert.True(match.Success, match.FailureReason);
        Assert.InRange(Math.Abs(match.CenterX - 129.5), 0, 1);
        Assert.InRange(Math.Abs(match.CenterY - 149.5), 0, 1);
        Assert.Equal(2, run.Nodes["blob"].Output<int>("BlobCount"));
        Assert.True(run.Nodes["decision"].Output<InspectionDecision>("Decision").IsOk);
        Assert.All(run.Nodes.Values, n => Assert.Equal(NodeExecutionStatus.Success, n.Status));
        if (preprocessing)
        {
            Assert.Equal(PixelFormat.RGB8, run.Nodes["image"].Output<ImageFrame>("Image").PixelFormat);
            Assert.Equal(PixelFormat.BGR8, run.Nodes["gaussian"].Output<ImageFrame>("ProcessedImage").PixelFormat);
        }
    }

    [Fact]
    public async Task TransformedRoiActuallyChangesBlobSearchAndReturnsOriginalCoordinates()
    {
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(Flow(false), _directory);
        Assert.True(run.Success, run.ErrorMessage);
        var transformed = run.Nodes["roi"].Output<RotatedRectRoi>("ROI");
        Assert.InRange(Math.Abs(transformed.CenterX - 205.5), 0, 1);
        Assert.InRange(Math.Abs(transformed.CenterY - 149.5), 0, 1);
        var image = run.Nodes["image"].Output<ImageFrame>("Image");
        var oldRegion = BlobDetector.Detect(image, ReferenceRoi, new() { Threshold = 180, MinArea = 20, MaxArea = 300 });
        Assert.Equal(0, oldRegion.Count);
        var blobs = run.Nodes["blob"].Output<BlobResults>("BlobResults");
        Assert.Equal(2, blobs.Count);
        Assert.All(blobs.Items, b => Assert.InRange(b.CenterX, 190, 222));
        Assert.All(blobs.Items, b => Assert.InRange(b.CenterY, 146, 154));
    }

    [Fact]
    public async Task MatchingFailureAfterSuccessDoesNotReusePoseOrBlobResults()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        using var good = await executor.ExecuteAsync(Flow(false), _directory);
        Assert.True(good.Success, good.ErrorMessage);
        using var bad = await executor.ExecuteAsync(Flow(false, "blank.png"), _directory);
        Assert.False(bad.Success);
        Assert.False(bad.Nodes["match"].Output<bool>("Success"));
        Assert.False(bad.Nodes["match"].Outputs.ContainsKey("Pose2D"));
        Assert.Equal(NodeExecutionStatus.Skipped, bad.Nodes["roi"].Status);
        Assert.Equal(NodeExecutionStatus.Skipped, bad.Nodes["blob"].Status);
        Assert.Empty(bad.Nodes["blob"].Outputs);
        Assert.Contains("unavailable", bad.Nodes["roi"].ErrorMessage);
    }

    [Fact]
    public async Task InvalidGaussianParametersFailNodeWithoutCrashingRuntime()
    {
        var graph = Flow(true);
        var nodes = graph.Nodes.Select(n => n.NodeId == "gaussian" ? n with { Parameters = JsonDefaults.Element(new GaussianBlurParameters { Options = new() { KernelSize = 4 } }) } : n);
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(new(nodes, graph.Connections), _directory);
        Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["gaussian"].Status);
        Assert.Contains("KernelSize", run.Nodes["gaussian"].ErrorMessage);
        Assert.Equal(NodeExecutionStatus.Skipped, run.Nodes["match"].Status);
        Assert.Empty(run.Nodes["gaussian"].Outputs);
    }

    [Fact]
    public async Task TwoGaussianNodesChainWithSeparateOutputsAndBorrowedOriginal()
    {
        var parameters = new GaussianBlurParameters { Options = new() { KernelSize = 3, SigmaX = .8 } };
        var graph = new NodeGraph([
            Spec("image", "ImageInput", new ImageInputParameters { Path = "scene.png" }),
            Spec("first", "GaussianBlur", parameters), Spec("second", "GaussianBlur", parameters)
        ], [Edge("image", "Image", "first", "Image"), Edge("first", "ProcessedImage", "second", "Image")]);
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(graph, _directory);
        Assert.True(run.Success, run.ErrorMessage);
        var original = run.Nodes["image"].Output<ImageFrame>("Image");
        var first = run.Nodes["first"].Output<ImageFrame>("ProcessedImage");
        var second = run.Nodes["second"].Output<ImageFrame>("ProcessedImage");
        Assert.NotSame(original.BufferHandle, first.BufferHandle);
        Assert.NotSame(first.BufferHandle, second.BufferHandle);
        using var expectedFirst = GaussianBlurProcessor.Process(original, parameters.Options);
        using var expectedSecond = GaussianBlurProcessor.Process(expectedFirst, parameters.Options);
        AssertPixelsEqual(expectedSecond, second);
        using var reload = ImageFile.Load(Path.Combine(_directory, "scene.png"));
        AssertPixelsEqual(reload, original);
    }

    [Fact]
    public async Task ImageBranchesShareOneReadonlyAllocation()
    {
        var registry = BuiltinRegistration.CreateRegistry();
        registry.Register("ReadProbe", () => new ReadProbeNode());
        var graph = new NodeGraph([
            Spec("image", "ImageInput", new ImageInputParameters { Path = "scene.png" }),
            Spec("left", "ReadProbe", new { }), Spec("right", "ReadProbe", new { })
        ], [Edge("image", "Image", "left", "Image"), Edge("image", "Image", "right", "Image")]);
        var run = await new RuntimeExecutor(registry).ExecuteAsync(graph, _directory);
        var image = run.Nodes["image"].Output<ImageFrame>("Image");
        Assert.Same(image.BufferHandle, run.Nodes["left"].Output<FrameHandle>("Handle"));
        Assert.Same(image.BufferHandle, run.Nodes["right"].Output<FrameHandle>("Handle"));
        using var retained = image.Share();
        run.Dispose();
        Assert.True(image.IsDisposed);
        using var lease = retained.Lease();
        Assert.Equal(45, lease.GetRowSpan(0)[0]);
    }

    [Fact]
    public async Task ExecuteToAndExplicitSingleMatchAgreeWithFullFlow()
    {
        var graph = Flow(true); var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        using var full = await executor.ExecuteAsync(graph, _directory);
        using var prefix = await executor.ExecuteToAsync(graph, "match", _directory);
        Assert.DoesNotContain("roi", prefix.Nodes.Keys);
        var image = prefix.Nodes["gaussian"].Output<ImageFrame>("ProcessedImage");
        using var single = await executor.ExecuteSingleAsync(graph.Nodes.Single(n => n.NodeId == "match"), new Dictionary<string, object> { ["Image"] = image }, _directory);
        var a = full.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
        var b = prefix.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
        var c = single.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
        Assert.True(a.Success && b.Success && c.Success);
        Assert.Equal(a.Score, b.Score); Assert.Equal(a.Score, c.Score);
        Assert.Equal(a.CenterX, c.CenterX); Assert.Equal(a.CenterY, c.CenterY);
    }

    [Fact]
    public async Task SavedReloadedProductionGraphGivesSameMeasurements()
    {
        var matcher = new CvInspectTemplateMatcher(); var registry = BuiltinRegistration.CreateRegistry();
        var graph = Flow(true);
        var project = new ProjectDocument
        {
            ProjectName = "Integration inspection", Nodes = graph.Nodes.ToList(), Connections = graph.Connections.ToList(),
            ROI = [ReferenceRoi], TemplateResources = [new("fixture", "template.json")],
            AlgorithmVersion = new() { [matcher.AlgorithmId] = matcher.Version }
        };
        var store = new ProjectStore(registry, new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
        var path = Path.Combine(_directory, "project.json");
        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);
        var executor = new RuntimeExecutor(registry);
        using var before = await executor.ExecuteAsync(project.ToGraph(), _directory);
        using var after = await executor.ExecuteAsync(loaded.ToGraph(), _directory);
        Assert.True(before.Success && after.Success, after.ErrorMessage);
        Assert.Equal(before.Nodes["match"].Output<TemplateMatchResult>("MatchResult").Score, after.Nodes["match"].Output<TemplateMatchResult>("MatchResult").Score);
        Assert.Equal(before.Nodes["roi"].Output<RotatedRectRoi>("ROI"), after.Nodes["roi"].Output<RotatedRectRoi>("ROI"));
        Assert.Equal(before.Nodes["blob"].Output<int>("BlobCount"), after.Nodes["blob"].Output<int>("BlobCount"));
    }

    private static void AssertPixelsEqual(ImageFrame expected, ImageFrame actual)
    {
        Assert.Equal(expected.Width, actual.Width); Assert.Equal(expected.Height, actual.Height); Assert.Equal(expected.PixelFormat, actual.PixelFormat);
        using var a = expected.Lease(); using var b = actual.Lease();
        for (var y = 0; y < expected.Height; y++) Assert.True(a.GetRowSpan(y).SequenceEqual(b.GetRowSpan(y)), $"Row {y} differs.");
    }
    private sealed class ReadProbeNode : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new("ReadProbe", "1.0.0", [new("Image", typeof(ImageFrame))], [new("Handle", typeof(FrameHandle))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Handle"] = context.Input<ImageFrame>("Image").BufferHandle }));
    }
    public void Dispose()
    {
        var path = Path.GetFullPath(_directory);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("VisionPlatform-Integration-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected integration test directory.");
        Directory.Delete(path, recursive: true);
    }
}

using System.Security.Cryptography;
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

/// <summary>Portable transport/contract tests. No mock detection is evidence of model inference accuracy.
/// Explicit external inference validation lives in tools/yolo_bridge/verify.py.</summary>
public sealed class Phase4BYoloTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionPlatform-YoloTest-" + Guid.NewGuid().ToString("N"));
    private readonly string _model;
    private readonly string _sha;
    private readonly string _script;
    private readonly string _python;
    public Phase4BYoloTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var path in new[] { "core/inference/backend.py", "core/inference/config.py", "core/inference/service.py" })
        { var file = Path.Combine(_root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "# transport fixture only"); }
        _model = Path.Combine(_root, "model.onnx"); File.WriteAllBytes(_model, [10, 20, 30]);
        _sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_model))).ToLowerInvariant();
        _script = Path.Combine(_root, "bridge.py"); File.WriteAllText(_script, "# never executed by mock runner");
        _python = Path.Combine(_root, "python.exe"); File.WriteAllBytes(_python, []);
        SaveSidecar();
    }

    private YoloInferenceOptions Options => new() { PythonExecutable = _python, YoloStudioRoot = _root, ModelPath = _model, ImageSize = 128 };
    private void SaveSidecar(string? sha = null) => File.WriteAllText(Path.Combine(_root, "deployment.json"), JsonSerializer.Serialize(new
    {
        schema_version = 1, task = "detect", model_file = "model.onnx", onnx_sha256 = sha ?? _sha,
        input = new { layout = "NCHW", channel_order = "RGB", dtype = "tensor(float)", dynamic = false, height = 128, width = 128 },
        output = new { format = "xyxy_conf_class" }, names = new Dictionary<string, string> { ["0"] = "fixture" }
    }));

    private string Response(int width = 8, int height = 6, double x = 1, bool empty = false, string? sha = null,
        string provider = "ONNX Runtime CPUExecutionProvider", int classId = 0) => JsonSerializer.Serialize(new
    {
        schema_version = 1, success = true, model_sha256 = sha ?? _sha, width, height, provider,
        tile_count = 1, inference_ms = 1.2,
        boxes = empty ? [] : new[] { new { class_id = classId, confidence = .8, x, y = 1d, w = 2d, h = 2d } }
    });

    private sealed class Runner(Func<string, CancellationToken, Task<string>> action) : IYoloBridgeRunner
    {
        public int Calls { get; private set; }
        public string? RequestDirectory { get; private set; }
        public Task<string> RunAsync(string pythonExecutable, string bridgeScript, string requestPath, int timeoutMilliseconds, CancellationToken ct)
        { Calls++; RequestDirectory = Path.GetDirectoryName(requestPath); return action(requestPath, ct); }
    }
    private PythonYoloInference Engine(Runner runner) => new(runner, _script);
    private static ImageFrame Image(PixelFormat format = PixelFormat.Mono8, ImageCoordinateSpace? space = null)
    {
        using var writer = new ImageFrameBuilder(8, 6, format, stride: format.BytesPerPixel() * 8 + 5, coordinateSpace: space);
        for (var row = 0; row < writer.Height; row++) writer.GetRowSpan(row).Fill(17);
        return writer.Build();
    }

    [Fact]
    public void EmptyPathsAreValidEditorDraftsButInvalidExecutionResources()
    {
        Assert.Empty(YoloInferenceValidation.Validate(new()));
        Assert.Empty(new YoloInferenceNode().ValidateParameters(JsonDefaults.Element(new YoloInferenceOptions())));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(31)] [InlineData(129)] [InlineData(4097)]
    public void InvalidImageSizesAreRejected(int size) => Assert.NotEmpty(YoloInferenceValidation.Validate(new() { ImageSize = size }));

    [Fact]
    public void RejectsNonfiniteAndMalformedParametersWithoutStartingProcess()
    {
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { Confidence = double.NaN }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { Iou = double.PositiveInfinity }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { Overlap = 1 }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { TileWidth = 1 }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { TimeoutMilliseconds = 0 }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { ClassIds = "0,,1" }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { ClassIds = "-1" }));
        Assert.NotEmpty(YoloInferenceValidation.Validate(new() { ClassIds = "0.5" }));
        Assert.Equal(new[] { 2, 1 }, YoloInferenceValidation.ParseClasses("2,1,2"));
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8)] [InlineData(PixelFormat.BayerGB8)]
    public async Task BayerRequiresExplicitConversion(PixelFormat format)
    {
        using var image = Image(format); var runner = new Runner((_, _) => Task.FromResult(Response()));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine(runner).InferAsync(image, Options).AsTask());
        Assert.Equal(0, runner.Calls); Assert.False(image.IsDisposed);
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)] [InlineData(PixelFormat.RGB8)] [InlineData(PixelFormat.BGR8)]
    public async Task SnapshotPreservesInputAndCleansOnlyItsOwnTemporaryDirectory(PixelFormat format)
    {
        using var image = Image(format);
        var runner = new Runner((request, _) =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(request));
            using var snapshot = ImageFile.Load(document.RootElement.GetProperty("image_path").GetString()!);
            Assert.Equal(image.Width, snapshot.Width); Assert.Equal(image.Height, snapshot.Height);
            using var lease = snapshot.Lease(); Assert.Equal(17, lease.GetRowSpan(0)[0]);
            return Task.FromResult(Response());
        });
        var result = await Engine(runner).InferAsync(image, Options);
        Assert.True(result.Success); Assert.Single(result.Detections); Assert.Single(result.Overlays);
        Assert.Equal(_sha, result.ModelSha256); Assert.Equal(1, result.TileCount); Assert.False(image.IsDisposed);
        Assert.False(Directory.Exists(runner.RequestDirectory)); Assert.True(File.Exists(_model));
    }

    [Fact]
    public async Task RgbSnapshotHasCorrectBgrDecoderChannelsAndDoesNotMutateSource()
    {
        using var writer = new ImageFrameBuilder(8, 6, PixelFormat.RGB8);
        writer.GetRowSpan(0)[0] = 210; writer.GetRowSpan(0)[1] = 90; writer.GetRowSpan(0)[2] = 30;
        using var image = writer.Build();
        var runner = new Runner((request, _) =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(request));
            using var snapshot = ImageFile.Load(document.RootElement.GetProperty("image_path").GetString()!);
            Assert.Equal(PixelFormat.BGR8, snapshot.PixelFormat);
            using var pixels = snapshot.Lease(); Assert.Equal(new byte[] { 30, 90, 210 }, pixels.GetRowSpan(0)[..3].ToArray());
            return Task.FromResult(Response());
        });
        await Engine(runner).InferAsync(image, Options);
        using var original = image.Lease(); Assert.Equal(new byte[] { 210, 90, 30 }, original.GetRowSpan(0)[..3].ToArray());
    }

    [Fact]
    public async Task RoiAndNestedViewRestoreSourceCoordinateOrigin()
    {
        using var image = Image(space: new("sensor", 100, 200)); using var view = image.CreateView(1, 1, 6, 4);
        var runner = new Runner((_, _) => Task.FromResult(Response(4, 3, x: 0)));
        var result = await Engine(runner).InferAsync(view, Options with { UseRoi = true, RoiX = 102, RoiY = 201, RoiWidth = 4, RoiHeight = 3 });
        Assert.Equal("sensor", result.CoordinateSpaceId);
        Assert.Equal(new RectRoi(102, 202, 2, 2), result.Detections[0].Bounds);
        Assert.Equal(new Point2D(102, 202), result.Overlays[0].Points[0]); Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task InputRoiTakesPrecedenceAndRotatedOrOutsideRoiReject()
    {
        using var image = Image(); var runner = new Runner((_, _) => Task.FromResult(Response(4, 4)));
        var result = await Engine(runner).InferAsync(image, Options with { UseRoi = true, RoiX = 999 }, new(2, 2, 4, 4));
        Assert.Single(result.Detections);
        await Assert.ThrowsAsync<ArgumentException>(() => Engine(runner).InferAsync(image, Options, new(2, 2, 4, 4, 30)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => Engine(runner).InferAsync(image, Options, new(0, 0, 4, 4)).AsTask());
    }

    [Fact]
    public async Task ZeroDetectionsAreCurrentSuccessfulEmptyResult()
    {
        using var image = Image(); var call = 0;
        var runner = new Runner((_, _) => Task.FromResult(Response(empty: call++ > 0)));
        var engine = Engine(runner); Assert.Single((await engine.InferAsync(image, Options)).Detections);
        var empty = await engine.InferAsync(image, Options);
        Assert.True(empty.Success); Assert.Empty(empty.Detections); Assert.Empty(empty.Overlays); Assert.Equal(2, runner.Calls);
    }

    [Theory]
    [InlineData("sha")] [InlineData("bounds")] [InlineData("dimensions")] [InlineData("provider")] [InlineData("class")] [InlineData("json")]
    public async Task InvalidExternalResultsNeverPublish(string failure)
    {
        using var image = Image();
        var runner = new Runner((_, _) => Task.FromResult(failure switch
        {
            "sha" => Response(sha: "wrong"), "bounds" => Response(x: 999), "dimensions" => Response(width: 9),
            "provider" => Response(provider: "silent CUDA fallback"), "class" => Response(classId: 1), _ => "bad json"
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => Engine(runner).InferAsync(image, Options).AsTask());
        Assert.False(Directory.Exists(runner.RequestDirectory)); Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task MissingModelSidecarMismatchedHashUnknownClassesAndSizeFailBeforeTransport()
    {
        using var image = Image(); var runner = new Runner((_, _) => Task.FromResult(Response())); var engine = Engine(runner);
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.InferAsync(image, new()).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => engine.InferAsync(image, Options with { ClassIds = "2" }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => engine.InferAsync(image, Options with { ImageSize = 256 }).AsTask());
        SaveSidecar("wrong"); await Assert.ThrowsAsync<InvalidDataException>(() => engine.InferAsync(image, Options).AsTask());
        File.Delete(Path.Combine(_root, "deployment.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.InferAsync(image, Options).AsTask());
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task CancellationAndTransportTimeoutReleaseSnapshotsAndBorrowedInput()
    {
        using var image = Image(); using var cancellation = new CancellationTokenSource();
        var runner = new Runner(async (_, ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return ""; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(runner).InferAsync(image, Options, cancellationToken: cancellation.Token).AsTask());
        Assert.False(Directory.Exists(runner.RequestDirectory)); Assert.False(image.IsDisposed);
        var timeout = new Runner((_, _) => throw new TimeoutException("transport fixture timeout"));
        await Assert.ThrowsAsync<TimeoutException>(() => Engine(timeout).InferAsync(image, Options).AsTask());
        Assert.False(Directory.Exists(timeout.RequestDirectory));
    }

    [Fact]
    public async Task RuntimeUsesCurrentInjectedInferenceAndRecordsClearFailure()
    {
        using var image = Image(); var registry = new NodeRegistry();
        registry.Register(() => new YoloInferenceNode(new StubInference())); var executor = new RuntimeExecutor(registry);
        var spec = new NodeSpecification("yolo", "YoloInference", "fixture", JsonDefaults.Element(new YoloInferenceOptions()));
        using var first = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image });
        Assert.Equal(NodeExecutionStatus.Success, first.Nodes["yolo"].Status);
        Assert.Equal(0, first.Nodes["yolo"].Outputs["Count"]); Assert.False(image.IsDisposed);
        var empty = new NodeRegistry(); empty.Register(() => new YoloInferenceNode());
        using var failed = await new RuntimeExecutor(empty).ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image });
        Assert.Equal(NodeExecutionStatus.Failed, failed.Nodes["yolo"].Status);
        Assert.Contains("PythonExecutable", failed.Nodes["yolo"].ErrorMessage!); Assert.Empty(failed.Nodes["yolo"].Outputs);
    }

    private sealed class StubInference : IYoloInference
    {
        public ValueTask<YoloInferenceResult> InferAsync(ImageFrame image, YoloInferenceOptions options, RotatedRectRoi? roi = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new YoloInferenceResult(true, [], image.CoordinateSpace.Id, TimeSpan.Zero, []));
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}

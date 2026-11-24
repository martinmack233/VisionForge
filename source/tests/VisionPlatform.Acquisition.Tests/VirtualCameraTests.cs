using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.Acquisition;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace VisionPlatform.Acquisition.Tests;

public sealed class VirtualCameraTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VisionStudio-Camera-Tests-" + Guid.NewGuid().ToString("N")));
    public VirtualCameraTests() => Directory.CreateDirectory(_root);
    private CameraOpenOptions Options(bool loop = true, double rate = 50, PixelFormat? format = null) => new() { FolderPath = _root, Loop = loop, FrameRate = rate, PixelFormat = format };
    private void Image(string name, byte value, PixelFormat format = PixelFormat.Mono8, int width = 24, int height = 18)
    {
        using var builder = new ImageFrameBuilder(width, height, format, width * format.BytesPerPixel() + 7);
        for (var row = 0; row < height; row++)
        {
            var pixels = builder.GetRowSpan(row); pixels.Fill(value);
            if (format is PixelFormat.RGB8 or PixelFormat.BGR8)
                for (var x = 0; x < width; x++) { pixels[x * 3] = value; pixels[x * 3 + 1] = (byte)(value + 10); pixels[x * 3 + 2] = (byte)(value + 20); }
        }
        using var image = builder.Build(); ImageFile.Save(image, Path.Combine(_root, name));
    }
    private static byte First(ImageFrame image) { using var lease = image.Lease(); return lease.GetRowSpan(0)[0]; }

    [Fact]
    public async Task ProviderEnumerationCapabilitiesAndRegistryDiscoveryRemainSdkFree()
    {
        var registry = new CameraProviderRegistry();
        var device = Assert.Single(await registry.EnumerateAsync()); Assert.Equal("virtual-folder", device.ProviderId); Assert.True(device.IsVirtual);
        Assert.Throws<ArgumentException>(() => registry.Register(new VirtualCameraProvider()));
        Assert.Throws<KeyNotFoundException>(() => registry.Create("missing", "folder"));
        Assert.Throws<ArgumentException>(() => registry.Create("virtual-folder", "missing"));
        Assert.Equal(1, registry.Discover(typeof(FixtureCameraProvider).Assembly));
        Assert.Equal(2, (await registry.EnumerateAsync()).Count);
        await using var camera = registry.Create("virtual-folder", "folder");
        Assert.Equal(5, camera.Capabilities.PixelFormats.Count);
        Assert.DoesNotContain(camera.Capabilities.Parameters, p => p.Name is "Exposure" or "Gain");
        Assert.All(camera.Capabilities.Parameters.Where(p => p.Name is "Width" or "Height" or "Stride" or "Sequence" or "FileCount" or "NextIndex"), p => Assert.False(p.Writable));
    }

    [Fact]
    public async Task SingleCaptureReadsInDeterministicOrderLoopsAndTransfersOwnership()
    {
        Image("02.png", 80); Image("01.png", 40); File.WriteAllText(Path.Combine(_root, "ignore.txt"), "not an image");
        Directory.CreateDirectory(Path.Combine(_root, "nested")); File.WriteAllText(Path.Combine(_root, "nested/bad.png"), "not scanned");
        var baseline = FrameHandle.ActiveBytes; await using var camera = new VirtualCameraDevice();
        Assert.Equal(CameraState.Closed, camera.State);
        await camera.OpenAsync(Options()); await camera.OpenAsync(Options()); Assert.Equal(CameraState.Open, camera.State);
        using var first = await camera.CaptureSingleAsync(); Assert.Equal(40, First(first)); Assert.Equal("01.png", camera.LastFrame!.SourceName); Assert.Equal(1, camera.LastFrame.Sequence);
        using var second = await camera.CaptureSingleAsync(); Assert.Equal(80, First(second)); Assert.Equal(2, camera.LastFrame.Sequence); Assert.NotEqual(first.FrameId, second.FrameId);
        using var third = await camera.CaptureSingleAsync(); Assert.Equal(40, First(third)); Assert.Equal(3, camera.LastFrame.Sequence);
        await camera.CloseAsync(); Assert.Equal(CameraState.Closed, camera.State); Assert.Null(camera.LastFrame);
        Assert.Equal(40, First(first)); Assert.Equal(3 * first.Width * first.Height, FrameHandle.ActiveBytes - baseline);
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public async Task ExplicitFiveFormatsAndChannelsSurviveRealFileCapture(PixelFormat format)
    {
        Image("image.png", 25, format); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options(format: format));
        using var frame = await camera.CaptureSingleAsync();
        Assert.Equal(format, frame.PixelFormat); Assert.Equal(24, frame.Width); Assert.Equal(18, frame.Height);
        Assert.Equal(frame.FrameId, camera.LastFrame!.FrameId); Assert.Equal(frame.Timestamp, camera.LastFrame.Timestamp);
        Assert.Equal(frame.Stride, camera.LastFrame.Stride); Assert.Equal(25, First(frame));
        if (format is PixelFormat.RGB8 or PixelFormat.BGR8) { using var lease = frame.Lease(); Assert.Equal(new byte[] { 25, 35, 45 }, lease.GetRowSpan(0)[..3].ToArray()); }
    }

    [Fact]
    public async Task AutoFormatRetainsMonoAndBgrWithoutGuessingBayer()
    {
        Image("a.png", 20); Image("b.png", 30, PixelFormat.BGR8); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options());
        using var mono = await camera.CaptureSingleAsync(); using var bgr = await camera.CaptureSingleAsync();
        Assert.Equal(PixelFormat.Mono8, mono.PixelFormat); Assert.Equal(PixelFormat.BGR8, bgr.PixelFormat);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedAlphaAndSixteenBitFilesAreRejectedByActualDecoder(bool alpha)
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var unsupported = new OpenCvSharp.Mat(12, 16, alpha ? OpenCvSharp.MatType.CV_8UC4 : OpenCvSharp.MatType.CV_16UC1, OpenCvSharp.Scalar.All(100)))
            Assert.True(OpenCvSharp.Cv2.ImWrite(Path.Combine(_root, "unsupported.png"), unsupported));
        await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options());
        await Assert.ThrowsAsync<NotSupportedException>(() => camera.CaptureSingleAsync());
        Assert.Equal(CameraState.Faulted, camera.State); Assert.Null(camera.LastFrame); Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task NonLoopSingleCaptureReportsEofAndNeverReturnsPreviousFrame()
    {
        Image("a.png", 40); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options(loop: false));
        using var first = await camera.CaptureSingleAsync(); var prior = camera.LastFrame;
        await Assert.ThrowsAsync<EndOfStreamException>(() => camera.CaptureSingleAsync());
        Assert.Same(prior, camera.LastFrame); Assert.Equal(CameraState.Open, camera.State); Assert.Contains("End", camera.LastError);
        await camera.SetParameterAsync("Loop", J(true)); using var next = await camera.CaptureSingleAsync(); Assert.NotEqual(first.FrameId, next.FrameId); Assert.Equal(2, camera.LastFrame!.Sequence);
    }

    [Fact]
    public async Task ContinuousCaptureEndsAtEofAndRespectsPacingWithoutQueueing()
    {
        Image("a.png", 10); Image("b.png", 20); Image("c.png", 30); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options(loop: false, rate: 20));
        var watch = Stopwatch.StartNew(); var values = new List<byte>(); var sequences = new List<long>();
        await camera.StartAcquisitionAsync();
        await foreach (var frame in camera.ReadFramesAsync()) using (frame) { values.Add(First(frame)); sequences.Add(camera.LastFrame!.Sequence); }
        Assert.Equal(new byte[] { 10, 20, 30 }, values); Assert.Equal(new long[] { 1, 2, 3 }, sequences);
        Assert.True(watch.ElapsedMilliseconds >= 80, "Configured 20 FPS pacing was bypassed.");
        Assert.Equal(CameraState.Open, camera.State); Assert.Contains("End", camera.LastError);
    }

    [Fact]
    public async Task StopCancelsPendingReadAndNewStreamRequiresPreviousEnumeratorDisposal()
    {
        Image("a.png", 70); var baseline = FrameHandle.ActiveBytes; await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options(rate: .1)); await camera.StartAcquisitionAsync();
        var frames = camera.ReadFramesAsync().GetAsyncEnumerator(); Assert.True(await frames.MoveNextAsync()); frames.Current.Dispose();
        var pending = frames.MoveNextAsync().AsTask(); Assert.False(pending.IsCompleted);
        var watch = Stopwatch.StartNew(); await camera.StopAcquisitionAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await frames.DisposeAsync(); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5)); Assert.Equal(CameraState.Open, camera.State);
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
        await camera.StartAcquisitionAsync(); await camera.StopAcquisitionAsync();
    }

    [Fact]
    public async Task ContinuousConsumerIsExclusiveAndSingleCaptureCannotReadStalePreview()
    {
        Image("a.png", 50); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options()); await camera.StartAcquisitionAsync();
        await using var stream = camera.ReadFramesAsync().GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync()); using var frame = stream.Current;
        await using var second = camera.ReadFramesAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.CaptureSingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.SetParameterAsync("Loop", J(false)));
        await camera.StopAcquisitionAsync(); Assert.Equal(50, First(frame));
    }

    [Fact]
    public async Task CallerCancellationReturnsOpenStateAndPreCancelledCaptureDoesNotAdvance()
    {
        Image("a.png", 30); Image("b.png", 80); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => camera.CaptureSingleAsync(cancelled.Token));
        Assert.Null(camera.LastFrame); using var first = await camera.CaptureSingleAsync(); Assert.Equal(30, First(first));
        using var streamCancellation = new CancellationTokenSource(); await camera.StartAcquisitionAsync();
        await using (var stream = camera.ReadFramesAsync(streamCancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync()); stream.Current.Dispose(); streamCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.MoveNextAsync().AsTask());
        }
        Assert.Equal(CameraState.Open, camera.State);
    }

    [Fact]
    public async Task TypedCapabilityParametersRejectStringsInvalidRangesAndUnsupportedHardwareParameters()
    {
        Image("a.png", 30); await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options());
        await camera.SetParameterAsync("FrameRate", J(12.5)); await camera.SetParameterAsync("Loop", J(false)); await camera.SetParameterAsync("PixelFormat", J("Mono8"));
        var p = await camera.ReadParametersAsync(); Assert.Equal(12.5, p["FrameRate"].GetDouble()); Assert.False(p["Loop"].GetBoolean()); Assert.Equal("Mono8", p["PixelFormat"].GetString());
        Assert.Equal(1, p["FileCount"].GetInt32()); Assert.Equal(0, p["Width"].GetInt32());
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("FrameRate", J("20")));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("FrameRate", J(0)));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("FrameRate", J(1001)));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("Loop", J("true")));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("PixelFormat", J("Mono16")));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("PixelFormat", J("1")));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("Exposure", J(10)));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.SetParameterAsync("Width", J(100)));
        Assert.Equal(12.5, (await camera.ReadParametersAsync())["FrameRate"].GetDouble());
        using var frame = await camera.CaptureSingleAsync(); Assert.Equal(24, (await camera.ReadParametersAsync())["Width"].GetInt32());
    }

    [Fact]
    public async Task MissingEmptyCorruptAndDeletedFilesFailClearlyWithoutFallback()
    {
        await using var camera = new VirtualCameraDevice();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => camera.OpenAsync(Options() with { FolderPath = Path.Combine(_root, "missing") }));
        Assert.Equal(CameraState.Faulted, camera.State); await camera.CloseAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => camera.OpenAsync(Options())); await camera.CloseAsync();
        File.WriteAllText(Path.Combine(_root, "a.png"), "corrupt bytes"); await camera.OpenAsync(Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => camera.CaptureSingleAsync()); Assert.Equal(CameraState.Faulted, camera.State); Assert.Null(camera.LastFrame); Assert.NotNull(camera.LastError);
        await camera.CloseAsync(); Image("a.png", 10); await camera.OpenAsync(Options()); File.Delete(Path.Combine(_root, "a.png"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => camera.CaptureSingleAsync()); Assert.Null(camera.LastFrame); Assert.Equal(CameraState.Faulted, camera.State);
    }

    [Fact]
    public async Task SessionReusesSameConfigurationReopensChangesAndRuntimeScopeDisposesOnlyOwnReferences()
    {
        Image("a.png", 10); Image("b.png", 20); var baseline = FrameHandle.ActiveBytes;
        await using var services = new RuntimeExecutionSession(); var cameras = CameraSession.For(services); Assert.Same(cameras, CameraSession.For(services));
        var device = await cameras.GetVirtualDeviceAsync("main", Options()); Assert.Same(device, await cameras.GetVirtualDeviceAsync("main", Options()));
        using var first = await device.CaptureSingleAsync();
        var changed = await cameras.GetVirtualDeviceAsync("main", Options(loop: false)); Assert.NotSame(device, changed); Assert.Equal(CameraState.Disposed, device.State); Assert.Equal(10, First(first));
        await changed.StartAcquisitionAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => cameras.GetVirtualDeviceAsync("main", Options())); await changed.StopAcquisitionAsync();
        await changed.SetParameterAsync("Loop", J(true));
        var restored = await cameras.GetVirtualDeviceAsync("main", Options(loop: false)); Assert.NotSame(changed, restored); // Direct parameter writes cannot silently override graph snapshots.
        await services.DisposeAsync(); Assert.Equal(CameraState.Disposed, restored.State); Assert.Equal(10, First(first));
        Assert.Equal(baseline + first.Width * first.Height, FrameHandle.ActiveBytes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cameras.GetVirtualDeviceAsync("main", Options()));
    }

    [Fact]
    public async Task GenericProviderAndDistinctCameraIdsKeepTheirOwnSequenceAndCanBeClosedIndividually()
    {
        Image("a.png", 10); Image("b.png", 20); var providers = new CameraProviderRegistry(); providers.Register(new FixtureCameraProvider());
        await using var cameras = new CameraSession(providers);
        var nativeSeam = await cameras.GetDeviceAsync("adapter", "fixture-camera", "fixture", Options());
        var first = await cameras.GetVirtualDeviceAsync("camera-a", Options()); var second = await cameras.GetVirtualDeviceAsync("camera-b", Options());
        using var adapterFrame = await nativeSeam.CaptureSingleAsync(); using var a1 = await first.CaptureSingleAsync(); using var b1 = await second.CaptureSingleAsync(); using var a2 = await first.CaptureSingleAsync();
        Assert.Equal(10, First(adapterFrame)); Assert.Equal(10, First(a1)); Assert.Equal(10, First(b1)); Assert.Equal(20, First(a2));
        Assert.Equal(2, first.LastFrame!.Sequence); Assert.Equal(1, second.LastFrame!.Sequence);
        await cameras.CloseDeviceAsync("camera-a"); Assert.Equal(CameraState.Disposed, first.State); Assert.Equal(CameraState.Open, second.State); Assert.Equal(10, First(a1));
        await cameras.CloseDeviceAsync("camera-a"); // Closing an already removed ID is safe.
    }

    [Fact]
    public async Task FolderParameterChangeRebuildsFileListAtomicallyAndResetsSequence()
    {
        Image("a.png", 10); var next = Path.Combine(_root, "other"); Directory.CreateDirectory(next);
        using (var image = new ImageFrameBuilder(30, 20, PixelFormat.Mono8).Build()) ImageFile.Save(image, Path.Combine(next, "other.png"));
        await using var camera = new VirtualCameraDevice(); await camera.OpenAsync(Options()); using var before = await camera.CaptureSingleAsync();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => camera.SetParameterAsync("FolderPath", J(Path.Combine(_root, "missing"))));
        Assert.Equal(_root, (await camera.ReadParametersAsync())["FolderPath"].GetString()); Assert.Equal(1, camera.LastFrame!.Sequence);
        await camera.SetParameterAsync("FolderPath", J(next)); Assert.Null(camera.LastFrame); Assert.Equal(0, (await camera.ReadParametersAsync())["Sequence"].GetInt64());
        using var after = await camera.CaptureSingleAsync(); Assert.Equal(30, after.Width); Assert.Equal(20, after.Height); Assert.Equal(1, camera.LastFrame!.Sequence); Assert.Equal(10, First(before));
    }

    [Fact]
    public async Task DisposingSessionCancelsContinuousReadAndConsumerFrameSurvives()
    {
        Image("a.png", 50); var cameras = new CameraSession(); var camera = await cameras.GetVirtualDeviceAsync("main", Options(rate: .1));
        await camera.StartAcquisitionAsync(); var stream = camera.ReadFramesAsync().GetAsyncEnumerator(); Assert.True(await stream.MoveNextAsync()); using var held = stream.Current;
        var pending = stream.MoveNextAsync().AsTask(); await cameras.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5))); await stream.DisposeAsync();
        Assert.Equal(CameraState.Disposed, camera.State); Assert.Equal(50, First(held)); await cameras.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedCaptureAndOpenCloseCyclesReturnAllPlatformBuffers()
    {
        Image("a.png", 40); var baseline = FrameHandle.ActiveBytes;
        await using (var camera = new VirtualCameraDevice())
            for (var cycle = 0; cycle < 12; cycle++)
            {
                await camera.OpenAsync(Options());
                for (var iteration = 0; iteration < 8; iteration++) using (var frame = await camera.CaptureSingleAsync()) Assert.Equal(40, First(frame));
                await camera.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
            }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task Real8192By10000MonoFileLoadsThroughVirtualCameraAndReleases()
    {
        Image("large.bmp", 77, width: 8192, height: 10000); var baseline = FrameHandle.ActiveBytes;
        await using (var camera = new VirtualCameraDevice())
        {
            await camera.OpenAsync(Options()); using var frame = await camera.CaptureSingleAsync();
            Assert.Equal(8192, frame.Width); Assert.Equal(10000, frame.Height); Assert.Equal(77, First(frame));
            Assert.Equal(baseline + 81920000, FrameHandle.ActiveBytes); await camera.CloseAsync(); Assert.Equal(77, First(frame));
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task VirtualCameraNodeUsesOriginalRuntimeAndEmptyDraftDoesNotAcquire()
    {
        Image("a.png", 30); Image("b.png", 70); var baseline = FrameHandle.ActiveBytes;
        var node = new VirtualCameraNode(); Assert.Empty(node.ValidateParameters(JsonDefaults.Element(new VirtualCameraParameters())));
        var registry = new NodeRegistry(); registry.Register("VirtualCamera", () => new VirtualCameraNode()); registry.Register("ImageInput", () => new ImageInputNode());
        await using var services = new RuntimeExecutionSession(); var executor = new RuntimeExecutor(registry, null, services);
        var spec = new NodeSpecification("camera", "VirtualCamera", "camera", JsonDefaults.Element(new VirtualCameraParameters { FolderPath = ".", Loop = false }));
        var graph = new NodeGraph([spec, new("input", "ImageInput", "input", JsonDefaults.Element(new ImageInputParameters()))], [new("camera", "Image", "input", "Image")]);
        using (var first = await executor.ExecuteAsync(graph, _root))
        {
            Assert.True(first.Success, first.ErrorMessage); Assert.Equal(30, First(first.Nodes["input"].Output<ImageFrame>("Image")));
            Assert.Equal(1, first.Nodes["camera"].Output<CameraFrameInfo>("FrameInfo").Sequence);
        }
        using (var next = await executor.ExecuteAsync(graph, _root)) { Assert.True(next.Success); Assert.Equal(70, First(next.Nodes["camera"].Output<ImageFrame>("Image"))); }
        using (var eof = await executor.ExecuteAsync(graph, _root)) { Assert.False(eof.Success); Assert.Equal(NodeExecutionStatus.Failed, eof.Nodes["camera"].Status); Assert.Empty(eof.Nodes["camera"].Outputs); Assert.Equal(NodeExecutionStatus.Skipped, eof.Nodes["input"].Status); }
        using (var empty = await executor.ExecuteSingleAsync(spec with { Parameters = JsonDefaults.Element(new VirtualCameraParameters()) }, new Dictionary<string, object>(), _root))
        { Assert.False(empty.Success); Assert.Contains("FolderPath", empty.Nodes["camera"].ErrorMessage); Assert.Empty(empty.Nodes["camera"].Outputs); }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    public void Dispose()
    {
        var full = Path.GetFullPath(_root); var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("VisionStudio-Camera-Tests-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
    }
}

public sealed class FixtureCameraProvider : ICameraProvider
{
    public string ProviderId => "fixture-camera";
    public Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CameraDeviceInfo>>([new("fixture", ProviderId, "fixture file provider", true)]);
    public ICameraDevice Create(string deviceId) => new VirtualCameraDevice(new(deviceId, ProviderId, "fixture file provider", true));
}

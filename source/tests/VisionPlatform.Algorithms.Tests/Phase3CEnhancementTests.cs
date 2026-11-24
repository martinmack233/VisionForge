using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase3CEnhancementTests
{
    [Theory]
    [InlineData(BinaryThresholdType.Binary, new byte[] { 0, 0, 200, 200 })]
    [InlineData(BinaryThresholdType.BinaryInv, new byte[] { 200, 200, 0, 0 })]
    public void OtsuCalculatesMeasuredThresholdAndAnalyticPixels(BinaryThresholdType type, byte[] expected)
    {
        using var input = FrameFactory.CopyFrom(new byte[] { 10, 10, 200, 200, 99, 99 }, 4, 1, PixelFormat.Mono8, 6);
        using var result = new OtsuThresholdProcessor().ProcessWithThreshold(input, new() { MaxValue = 200, ThresholdType = type });
        Assert.Equal(10, result.CalculatedThreshold); Assert.Equal(expected, Pixels(result.BinaryImage));
        Assert.Equal(new byte[] { 10, 10, 200, 200 }, Pixels(input)); Assert.NotSame(input.BufferHandle, result.BinaryImage.BufferHandle);
    }

    [Theory]
    [InlineData(AdaptiveThresholdMethod.Mean, BinaryThresholdType.Binary)]
    [InlineData(AdaptiveThresholdMethod.Gaussian, BinaryThresholdType.Binary)]
    [InlineData(AdaptiveThresholdMethod.Mean, BinaryThresholdType.BinaryInv)]
    [InlineData(AdaptiveThresholdMethod.Gaussian, BinaryThresholdType.BinaryInv)]
    public void AdaptiveMethodsAndInversionMatchNativeReference(AdaptiveThresholdMethod method, BinaryThresholdType type)
    {
        using var builder = Pattern(13, 11, stride: 19); using var input = builder.Build(); var before = Pixels(input);
        var options = new AdaptiveThresholdOptions { Method = method, ThresholdType = type, BlockSize = 5, C = 3.5, MaxValue = 203 };
        using var actual = new AdaptiveThresholdProcessor().Process(input, options);
        using var source = new OpenCvImageLease(input); using var expected = new Mat();
        Cv2.AdaptiveThreshold(source.Mat, expected, options.MaxValue,
            method == AdaptiveThresholdMethod.Mean ? AdaptiveThresholdTypes.MeanC : AdaptiveThresholdTypes.GaussianC,
            type == BinaryThresholdType.Binary ? ThresholdTypes.Binary : ThresholdTypes.BinaryInv, 5, 3.5);
        using var expectedFrame = FrameFactory.CopyFromMat(expected, PixelFormat.Mono8);
        Assert.Equal(Pixels(expectedFrame), Pixels(actual)); Assert.Equal(before, Pixels(input)); Assert.Equal(PixelFormat.Mono8, actual.PixelFormat);
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void NormalizeUsesOneGlobalMinMaxWithoutSwappingChannels(PixelFormat format)
    {
        var width = format == PixelFormat.Mono8 ? 3 : 1;
        using var input = FrameFactory.CopyFrom(new byte[] { 10, 60, 110, 99, 99 }, width, 1, format, 5);
        using var output = new NormalizeProcessor().Process(input, new() { Minimum = 20, Maximum = 220 });
        Assert.Equal(new byte[] { 20, 120, 220 }, Pixels(output)); Assert.Equal(new byte[] { 10, 60, 110 }, Pixels(input));
        Assert.Equal(format, output.PixelFormat); Assert.NotSame(input.BufferHandle, output.BufferHandle);
        Assert.Equal(input.CoordinateSpace, output.CoordinateSpace);
    }

    [Fact]
    public void ConstantNormalizeMapsToMinimumAndEqualizePreservesConstant()
    {
        using var input = FrameFactory.CopyFrom(Enumerable.Repeat((byte)55, 16).ToArray(), 4, 4, PixelFormat.Mono8);
        using var normalized = new NormalizeProcessor().Process(input, new() { Minimum = 20, Maximum = 220 });
        using var equalized = new EqualizeHistProcessor().Process(input, new());
        Assert.All(Pixels(normalized), p => Assert.Equal(20, p)); Assert.Equal(Pixels(input), Pixels(equalized));
    }

    [Fact]
    public void EqualizeHasAnalyticHistogramAndRoiUsesOnlySelectedRegion()
    {
        using var input = FrameFactory.CopyFrom(new byte[] { 10, 10, 20, 40 }, 4, 1, PixelFormat.Mono8);
        using var output = new EqualizeHistProcessor().Process(input, new());
        Assert.Equal(new byte[] { 0, 0, 128, 255 }, Pixels(output));
        using var builder = new ImageFrameBuilder(10, 8, PixelFormat.Mono8, 17, new("sensor", 10, 20));
        for (var y = 0; y < 8; y++) builder.GetRowSpan(y).Fill(250);
        for (var y = 3; y < 6; y++) new byte[] { 10, 10, 20, 40 }.CopyTo(builder.GetRowSpan(y).Slice(3, 4));
        using var full = builder.Build(); using var view = full.CreateView(1, 1, 8, 6);
        using var roi = new EqualizeHistProcessor().Process(view, new() { ROI = new RectRoi(13, 23, 4, 3) });
        Assert.Equal(new ImageCoordinateSpace("sensor", 13, 23), roi.CoordinateSpace); Assert.Equal(4, roi.Width); Assert.Equal(3, roi.Height);
        using var lease = roi.Lease(); Assert.Equal(new byte[] { 0, 0, 128, 255 }, lease.GetRowSpan(1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqualizeHistProcessor().Process(view, new() { ROI = new RectRoi(18, 22, 2, 2) }));
    }

    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(4, 2.0)]
    [InlineData(8, 8.0)]
    public void ClaheMatchesNativeReferenceAndPreservesInput(int grid, double clip)
    {
        using var builder = Pattern(31, 29, stride: 37); using var input = builder.Build(); var original = Pixels(input);
        using var actual = new ClaheProcessor().Process(input, new() { TileGridSize = grid, ClipLimit = clip });
        using var source = new OpenCvImageLease(input); using var expected = new Mat(); using var algorithm = Cv2.CreateCLAHE(clip, new(grid, grid));
        algorithm.Apply(source.Mat, expected); using var reference = FrameFactory.CopyFromMat(expected, PixelFormat.Mono8);
        Assert.Equal(Pixels(reference), Pixels(actual)); Assert.Equal(original, Pixels(input)); Assert.NotSame(input.BufferHandle, actual.BufferHandle);
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void GrayscaleAlgorithmsRejectColorAndBayerExplicitly(PixelFormat format)
    {
        using var input = new ImageFrameBuilder(12, 12, format).Build();
        Assert.Throws<NotSupportedException>(() => new OtsuThresholdProcessor().Process(input, new()));
        Assert.Throws<NotSupportedException>(() => new AdaptiveThresholdProcessor().Process(input, new()));
        Assert.Throws<NotSupportedException>(() => new EqualizeHistProcessor().Process(input, new()));
        Assert.Throws<NotSupportedException>(() => new ClaheProcessor().Process(input, new()));
        if (format.IsBayer()) Assert.Throws<NotSupportedException>(() => new NormalizeProcessor().Process(input, new()));
        Assert.False(input.IsDisposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(257)]
    public void AdaptiveRejectsIllegalBlockSizes(int block)
        => Assert.NotEmpty(new AdaptiveThresholdProcessor().Validate(new() { BlockSize = block }));

    [Fact]
    public void AllAlgorithmsValidateFiniteRangesAndEnumValues()
    {
        var otsu = new OtsuThresholdProcessor(); Assert.NotEmpty(otsu.Validate(new() { MaxValue = double.NaN }));
        Assert.NotEmpty(otsu.Validate(new() { ThresholdType = (BinaryThresholdType)17 }));
        var adaptive = new AdaptiveThresholdProcessor(); Assert.NotEmpty(adaptive.Validate(new() { C = double.PositiveInfinity }));
        Assert.NotEmpty(adaptive.Validate(new() { C = -256 })); Assert.NotEmpty(adaptive.Validate(new() { Method = (AdaptiveThresholdMethod)17 }));
        var normalize = new NormalizeProcessor(); Assert.NotEmpty(normalize.Validate(new() { Minimum = 255, Maximum = 255 }));
        Assert.NotEmpty(normalize.Validate(new() { Minimum = -1 })); Assert.NotEmpty(normalize.Validate(new() { Maximum = 256 }));
        Assert.NotEmpty(normalize.Validate(new() { Maximum = double.NaN }));
        var clahe = new ClaheProcessor(); Assert.NotEmpty(clahe.Validate(new() { ClipLimit = 0 }));
        Assert.NotEmpty(clahe.Validate(new() { ClipLimit = double.NaN })); Assert.NotEmpty(clahe.Validate(new() { TileGridSize = 65 }));
        Assert.NotEmpty(new EqualizeHistProcessor().Validate(new() { ROI = new RectRoi(0, 0, .5, 3) }));
    }

    [Fact]
    public void CancellationAndFailureNeverDisposeInputOrRetainOutputAllocations()
    {
        var before = FrameHandle.ActiveBytes;
        using (var builder = Pattern(20, 20))
        using (var input = builder.Build())
        {
            var baseline = FrameHandle.ActiveBytes; using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => new OtsuThresholdProcessor().Process(input, new(), cancellationToken: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => new AdaptiveThresholdProcessor().Process(input, new(), cancellationToken: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => new NormalizeProcessor().Process(input, new(), cancellationToken: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => new EqualizeHistProcessor().Process(input, new(), cancellationToken: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => new ClaheProcessor().Process(input, new(), cancellationToken: cancellation.Token));
            Assert.Throws<ArgumentException>(() => new ClaheProcessor().Process(input, new() { ClipLimit = double.NaN }));
            for (var i = 0; i < 30; i++)
            {
                using var normalized = new NormalizeProcessor().Process(input, new());
                using var otsu = new OtsuThresholdProcessor().ProcessWithThreshold(normalized, new());
                using var enhanced = new ClaheProcessor().Process(input, new());
            }
            Assert.Equal(baseline, FrameHandle.ActiveBytes); Assert.False(input.IsDisposed);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task NewNodesRegisterAndExecuteThroughExistingRuntimeWithTypedThresholdPort()
    {
        var before = FrameHandle.ActiveBytes;
        using (var builder = Pattern(20, 20))
        using (var input = builder.Build())
        {
            var registry = BuiltinRegistration.CreateRegistry(); var executor = new RuntimeExecutor(registry);
            foreach (var (type, options, port) in new (string, object, string)[] { ("OtsuThreshold", new OtsuThresholdOptions(), "BinaryImage"),
                ("AdaptiveThreshold", new AdaptiveThresholdOptions(), "BinaryImage"), ("Normalize", new NormalizeOptions(), "ProcessedImage"),
                ("EqualizeHist", new EqualizeHistOptions(), "ProcessedImage"), ("CLAHE", new ClaheOptions(), "ProcessedImage") })
            {
                using var run = await executor.ExecuteSingleAsync(new(type, type, type, JsonDefaults.Element(options)), new Dictionary<string, object> { ["Image"] = input });
                Assert.True(run.Success, run.ErrorMessage); Assert.Equal(PixelFormat.Mono8, run.Nodes[type].Output<ImageFrame>(port).PixelFormat);
                if (type == "OtsuThreshold") Assert.InRange(run.Nodes[type].Output<double>("CalculatedThreshold"), 0, 255);
            }
            using var invalid = await executor.ExecuteSingleAsync(new("bad", "AdaptiveThreshold", "bad", JsonDefaults.Element(new AdaptiveThresholdOptions { BlockSize = 4 })), new Dictionary<string, object> { ["Image"] = input });
            Assert.Equal(NodeExecutionStatus.Failed, invalid.Nodes["bad"].Status); Assert.Contains("BlockSize", invalid.Nodes["bad"].ErrorMessage);
            var mismatch = new NodeGraph([new("otsu", "OtsuThreshold", "otsu", JsonDefaults.Element(new OtsuThresholdOptions())),
                new("display", "TextDisplay", "display", JsonDefaults.Element(new TextDisplayParameters()))], [new("otsu", "BinaryImage", "display", "Number")]);
            Assert.Contains(new GraphValidator(registry).Validate(mismatch), e => e.Contains("mismatch"));
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    private static ImageFrameBuilder Pattern(int width, int height, int? stride = null)
    {
        var builder = new ImageFrameBuilder(width, height, PixelFormat.Mono8, stride);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) builder.GetRowSpan(y)[x] = (byte)((x * 17 + y * 23) % 256);
        return builder;
    }
    private static byte[] Pixels(ImageFrame image)
    {
        var bytes = new byte[image.Width * image.Height * image.PixelFormat.BytesPerPixel()]; using var lease = image.Lease();
        for (var y = 0; y < image.Height; y++) lease.GetRowSpan(y).CopyTo(bytes.AsSpan(y * image.Width * image.PixelFormat.BytesPerPixel()));
        return bytes;
    }
}

using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public class Phase3BasicProcessingTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void MedianRemovesImpulsePreservesDeclaredFormatAndDoesNotMutateSource(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(9, 9, format, 9 * format.BytesPerPixel() + 7);
        for (var y = 0; y < 9; y++)
            for (var x = 0; x < 9; x++)
                for (var c = 0; c < format.BytesPerPixel(); c++) builder.GetRowSpan(y)[x * format.BytesPerPixel() + c] = (byte)(30 + c * 50);
        builder.GetRowSpan(4)[4 * format.BytesPerPixel()] = 255;
        using var input = builder.Build();
        using var output = new MedianBlurProcessor().Process(input, new());
        using var source = input.Lease(); using var actual = output.Lease();
        Assert.Equal(255, source.GetRowSpan(4)[4 * format.BytesPerPixel()]);
        for (var c = 0; c < format.BytesPerPixel(); c++) Assert.Equal(30 + c * 50, actual.GetRowSpan(4)[4 * format.BytesPerPixel() + c]);
        Assert.Equal(format, output.PixelFormat); Assert.NotSame(input.BufferHandle, output.BufferHandle);
        using var identity = new MedianBlurProcessor().Process(input, new() { KernelSize = 1 });
        using var id = identity.Lease(); Assert.Equal(255, id.GetRowSpan(4)[4 * format.BytesPerPixel()]);
        Assert.NotSame(input.BufferHandle, identity.BufferHandle);
    }

    [Theory]
    [InlineData(ThresholdMode.Binary, new byte[] { 0, 0, 0, 200, 200 })]
    [InlineData(ThresholdMode.BinaryInv, new byte[] { 200, 200, 200, 0, 0 })]
    [InlineData(ThresholdMode.Trunc, new byte[] { 0, 99, 100, 100, 100 })]
    [InlineData(ThresholdMode.ToZero, new byte[] { 0, 0, 0, 101, 255 })]
    [InlineData(ThresholdMode.ToZeroInv, new byte[] { 0, 99, 100, 0, 0 })]
    public void EveryThresholdModeHasMeasuredBoundaryBehavior(ThresholdMode mode, byte[] expected)
    {
        using var input = FrameFactory.CopyFrom(new byte[] { 0, 99, 100, 101, 255, 77, 77 }, 5, 1, PixelFormat.Mono8, 7);
        using var output = new ThresholdProcessor().Process(input, new() { ThresholdValue = 100, MaxValue = 200, ThresholdType = mode });
        using var lease = output.Lease(); using var source = input.Lease();
        Assert.Equal(expected, lease.GetRowSpan(0)[..5].ToArray());
        Assert.Equal(expected.Count(v => v != 0), ThresholdProcessor.CountForeground(output));
        Assert.Equal(new byte[] { 0, 99, 100, 101, 255 }, source.GetRowSpan(0)[..5].ToArray());
        Assert.Equal(PixelFormat.Mono8, output.PixelFormat);
    }

    [Theory]
    [InlineData(MorphologyOperation.Erode, 9)]
    [InlineData(MorphologyOperation.Dilate, 49)]
    [InlineData(MorphologyOperation.Open, 25)]
    [InlineData(MorphologyOperation.Close, 25)]
    [InlineData(MorphologyOperation.Gradient, 40)]
    public void MorphologyOperationsProduceAnalyticRectangleAreas(MorphologyOperation operation, int area)
    {
        using var builder = new ImageFrameBuilder(13, 13, PixelFormat.Mono8);
        for (var y = 4; y < 9; y++) builder.GetRowSpan(y).Slice(4, 5).Fill(255);
        using var input = builder.Build();
        using var output = new MorphologyProcessor().Process(input, new() { Operation = operation });
        Assert.Equal(area, ThresholdProcessor.CountForeground(output));
        Assert.Equal(25, ThresholdProcessor.CountForeground(input));
    }

    [Theory]
    [InlineData(MorphologyKernelShape.Rectangle, 9, 25)]
    [InlineData(MorphologyKernelShape.Cross, 5, 13)]
    [InlineData(MorphologyKernelShape.Ellipse, 5, 13)]
    public void KernelShapesAndIterationsAreApplied(MorphologyKernelShape shape, int firstArea, int secondArea)
    {
        using var builder = new ImageFrameBuilder(11, 11, PixelFormat.Mono8); builder.GetRowSpan(5)[5] = 255;
        using var input = builder.Build();
        var options = new MorphologyOptions { Operation = MorphologyOperation.Dilate, KernelShape = shape };
        using var once = new MorphologyProcessor().Process(input, options);
        using var twice = new MorphologyProcessor().Process(input, options with { Iterations = 2 });
        Assert.Equal(firstArea, ThresholdProcessor.CountForeground(once));
        Assert.Equal(secondArea, ThresholdProcessor.CountForeground(twice));
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void ColorMorphologyDoesNotSwapOrMergeChannels(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(9, 9, format);
        builder.GetRowSpan(4)[4 * 3] = 130; builder.GetRowSpan(4)[4 * 3 + 1] = 70;
        using var input = builder.Build();
        using var output = new MorphologyProcessor().Process(input, new() { Operation = MorphologyOperation.Dilate });
        using var lease = output.Lease();
        Assert.Equal(format, output.PixelFormat);
        Assert.Equal(new byte[] { 130, 70, 0 }, lease.GetRowSpan(3).Slice(3 * 3, 3).ToArray());
    }

    [Fact]
    public void ExplicitRoiHasAnIsolatedBorderAndOriginalOrigin()
    {
        using var builder = new ImageFrameBuilder(11, 11, PixelFormat.Mono8);
        for (var y = 0; y < 11; y++) builder.GetRowSpan(y).Fill(255);
        for (var y = 3; y < 8; y++) builder.GetRowSpan(y).Slice(3, 5).Clear();
        using var input = builder.Build(); using var view = input.CreateView(1, 1, 9, 9);
        using var median = new MedianBlurProcessor().Process(view, new(), new RectRoi(3, 3, 5, 5));
        using var dilate = new MorphologyProcessor().Process(view, new() { Operation = MorphologyOperation.Dilate }, new RectRoi(3, 3, 5, 5));
        Assert.Equal(0, ThresholdProcessor.CountForeground(median)); Assert.Equal(0, ThresholdProcessor.CountForeground(dilate));
        Assert.Equal(new ImageCoordinateSpace("image", 3, 3), median.CoordinateSpace);
        using var whole = new MorphologyProcessor().Process(input, new() { Operation = MorphologyOperation.Dilate });
        using var laterCrop = whole.CreateView(3, 3, 5, 5);
        Assert.True(ThresholdProcessor.CountForeground(laterCrop) > 0);
    }

    [Fact]
    public void InvalidParametersFormatsAndCancellationDoNotAllocateOutputs()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var input = new ImageFrameBuilder(11, 11, PixelFormat.Mono8).Build())
        using (var bayer = new ImageFrameBuilder(11, 11, PixelFormat.BayerRG8).Build())
        using (var rgb = new ImageFrameBuilder(11, 11, PixelFormat.RGB8).Build())
        {
            var active = FrameHandle.ActiveBytes;
            foreach (var kernel in new[] { -1, 0, 2, 257 })
                Assert.Throws<ArgumentException>(() => new MedianBlurProcessor().Process(input, new() { KernelSize = kernel }));
            Assert.Throws<ArgumentException>(() => new ThresholdProcessor().Process(input, new() { ThresholdValue = double.NaN }));
            Assert.Throws<ArgumentException>(() => new ThresholdProcessor().Process(input, new() { MaxValue = 256 }));
            Assert.Throws<ArgumentException>(() => new ThresholdProcessor().Process(input, new() { ThresholdType = (ThresholdMode)90 }));
            Assert.Throws<ArgumentException>(() => new MorphologyProcessor().Process(input, new() { KernelWidth = 0 }));
            Assert.Throws<ArgumentException>(() => new MorphologyProcessor().Process(input, new() { KernelHeight = 256 }));
            Assert.Throws<ArgumentException>(() => new MorphologyProcessor().Process(input, new() { Iterations = 0 }));
            Assert.Throws<ArgumentException>(() => new MorphologyProcessor().Process(input, new() { Operation = (MorphologyOperation)90 }));
            Assert.Throws<ArgumentException>(() => new MorphologyProcessor().Process(input, new() { KernelShape = (MorphologyKernelShape)90 }));
            Assert.Throws<NotSupportedException>(() => new MedianBlurProcessor().Process(bayer, new()));
            Assert.Throws<NotSupportedException>(() => new MorphologyProcessor().Process(bayer, new()));
            Assert.Throws<NotSupportedException>(() => new ThresholdProcessor().Process(rgb, new()));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => new MedianBlurProcessor().Process(input, new(), cancellationToken: cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => new MorphologyProcessor().Process(input, new(), cancellationToken: cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => new ThresholdProcessor().Process(input, new(), cancellationToken: cancelled.Token));
            Assert.Equal(active, FrameHandle.ActiveBytes);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task RegisteredProcessingNodesExecuteIndividuallyWithExplicitSnapshotAndReleaseResources()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var builder = new ImageFrameBuilder(17, 17, PixelFormat.Mono8))
        {
            for (var y = 5; y < 12; y++) builder.GetRowSpan(y).Slice(5, 7).Fill(240);
            using var input = builder.Build();
            var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
            using var median = await executor.ExecuteSingleAsync(Spec("median", "MedianBlur", new MedianBlurOptions()), new Dictionary<string, object> { ["Image"] = input });
            Assert.True(median.Success);
            using var threshold = await executor.ExecuteSingleAsync(Spec("threshold", "Threshold", new ThresholdOptions()),
                new Dictionary<string, object> { ["Image"] = median.Nodes["median"].Output<ImageFrame>("ProcessedImage") });
            Assert.True(threshold.Success); Assert.Equal(45, threshold.Nodes["threshold"].Output<int>("ForegroundPixelCount"));
            using var morphology = await executor.ExecuteSingleAsync(Spec("morph", "Morphology", new MorphologyOptions()),
                new Dictionary<string, object> { ["Image"] = threshold.Nodes["threshold"].Output<ImageFrame>("ProcessedImage") });
            Assert.True(morphology.Success);
            using var invalid = await executor.ExecuteSingleAsync(Spec("bad", "MedianBlur", new MedianBlurOptions { KernelSize = 2 }), new Dictionary<string, object> { ["Image"] = input });
            Assert.False(invalid.Success); Assert.NotEmpty(invalid.Nodes["bad"].ErrorMessage!);
            using var source = input.Lease(); Assert.Equal(240, source.GetRowSpan(6)[6]);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    private static NodeSpecification Spec<T>(string id, string type, T parameters) => new(id, type, id, JsonDefaults.Element(parameters));
}

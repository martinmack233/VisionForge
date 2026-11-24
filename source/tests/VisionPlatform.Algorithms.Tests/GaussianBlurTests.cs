using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionPlatform.Algorithms.Tests;

public class GaussianBlurTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void ImpulseFilterProducesExpectedIndependentChannelsAndReadOnlySource(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(9, 9, format, stride: 9 * format.BytesPerPixel() + 5);
        var channelCount = format.BytesPerPixel();
        builder.GetRowSpan(4)[4 * channelCount] = 255;
        if (channelCount == 3) builder.GetRowSpan(4)[4 * channelCount + 1] = 100;
        using var input = builder.Build();
        using var result = GaussianBlurProcessor.Process(input, new GaussianBlurOptions { KernelSize = 3, SigmaX = 1, SigmaY = 1 });
        using var source = input.Lease(); using var filtered = result.Lease();
        Assert.Equal(255, source.GetRowSpan(4)[4 * channelCount]);
        Assert.Equal(52, filtered.GetRowSpan(4)[4 * channelCount]);
        Assert.InRange(filtered.GetRowSpan(4)[3 * channelCount], (byte)1, (byte)51);
        if (channelCount == 3)
        {
            // OpenCV's 8-bit separable kernel uses fixed-point coefficients; allow one intensity
            // level around the independently calculated normalized Gaussian response.
            var centerWeight = 1 / (1 + 2 * Math.Exp(-0.5));
            var idealGreen = 100 * centerWeight * centerWeight;
            Assert.InRange(Math.Abs(filtered.GetRowSpan(4)[4 * channelCount + 1] - idealGreen), 0, 1);
            Assert.Equal(0, filtered.GetRowSpan(4)[4 * channelCount + 2]);
        }
        Assert.Equal(format, result.PixelFormat); Assert.NotSame(input.BufferHandle, result.BufferHandle);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(-3)]
    public void InvalidKernelsAreRejectedBeforeNativeExecution(int size)
    {
        using var input = new ImageFrameBuilder(8, 8, PixelFormat.Mono8).Build();
        var before = FrameHandle.ActiveBytes;
        var options = new GaussianBlurOptions { KernelSize = size };
        Assert.Contains(GaussianBlurProcessor.Validate(options), e => e.Contains("KernelSize"));
        Assert.Throws<ArgumentException>(() => GaussianBlurProcessor.Process(input, options));
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public void SigmaAndBorderValidationRejectNaNInfinityNegativeAndUnknownEnums()
    {
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -0.001 })
        {
            Assert.NotEmpty(GaussianBlurProcessor.Validate(new GaussianBlurOptions { SigmaX = bad }));
            Assert.NotEmpty(GaussianBlurProcessor.Validate(new GaussianBlurOptions { SigmaY = bad }));
        }
        Assert.NotEmpty(GaussianBlurProcessor.Validate(new GaussianBlurOptions { BorderType = (GaussianBorderType)100 }));
        Assert.Empty(GaussianBlurProcessor.Validate(new GaussianBlurOptions { SigmaX = 0, SigmaY = 0, KernelSize = 1 }));
    }

    [Theory]
    [InlineData(GaussianBorderType.Constant)]
    [InlineData(GaussianBorderType.Replicate)]
    [InlineData(GaussianBorderType.Reflect)]
    [InlineData(GaussianBorderType.Reflect101)]
    public void SupportedBorderTypesAndAutomaticSigmaExecute(GaussianBorderType border)
    {
        using var input = FrameFactory.CopyFrom(Enumerable.Repeat((byte)120, 64).ToArray(), 8, 8, PixelFormat.Mono8);
        using var output = GaussianBlurProcessor.Process(input, new GaussianBlurOptions { SigmaX = 0, SigmaY = 0, BorderType = border });
        using var p = output.Lease();
        Assert.Equal(120, p.GetRowSpan(4)[4]);
        if (border == GaussianBorderType.Constant) Assert.True(p.GetRowSpan(0)[0] < 120);
        else Assert.Equal(120, p.GetRowSpan(0)[0]);
    }

    [Fact]
    public void RoiFilteringIsIsolatedAndDifferentFromFilteringWholeImageThenCropping()
    {
        using var builder = new ImageFrameBuilder(7, 7, PixelFormat.Mono8);
        for (var y = 0; y < 7; y++) builder.GetRowSpan(y).Fill(255);
        for (var y = 2; y < 5; y++) builder.GetRowSpan(y).Slice(2, 3).Clear();
        using var image = builder.Build();
        var options = new GaussianBlurOptions { KernelSize = 3, SigmaX = 1 };
        using var isolated = GaussianBlurProcessor.Process(image, options, new RectRoi(2, 2, 3, 3));
        using var whole = GaussianBlurProcessor.Process(image, options);
        using var fullCrop = whole.CreateView(2, 2, 3, 3);
        using var a = isolated.Lease(); using var b = fullCrop.Lease();
        Assert.Equal(3, isolated.Width); Assert.Equal(3, isolated.Height);
        Assert.Equal(2, isolated.CoordinateSpace.OriginX); Assert.Equal(2, isolated.CoordinateSpace.OriginY);
        Assert.Equal(0, a.GetRowSpan(0)[0]); Assert.True(b.GetRowSpan(0)[0] > 0);
        Assert.Equal(image.FrameId, isolated.FrameId);
    }

    [Fact]
    public void GaussianRoiUsesOriginalCoordinatesForNestedViews()
    {
        using var image = new ImageFrameBuilder(20, 20, PixelFormat.Mono8).Build();
        using var view = image.CreateView(8, 9, 10, 10);
        using var result = GaussianBlurProcessor.Process(view, new GaussianBlurOptions(), new RectRoi(10, 11, 4, 5));
        Assert.Equal(4, result.Width); Assert.Equal(5, result.Height);
        Assert.Equal(10, result.CoordinateSpace.OriginX); Assert.Equal(11, result.CoordinateSpace.OriginY);
    }

    [Fact]
    public void InvalidRoiAndBayerRequireExplicitHandling()
    {
        using var mono = new ImageFrameBuilder(10, 10, PixelFormat.Mono8).Build();
        using var bayer = new ImageFrameBuilder(10, 10, PixelFormat.BayerRG8).Build();
        Assert.Throws<NotSupportedException>(() => GaussianBlurProcessor.Process(bayer, new GaussianBlurOptions()));
        Assert.Throws<ArgumentException>(() => GaussianBlurProcessor.Process(mono, new GaussianBlurOptions(), new RectRoi(1.5, 1, 2, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GaussianBlurProcessor.Process(mono, new GaussianBlurOptions(), new RectRoi(9, 1, 2, 2)));
    }

    [Fact]
    public void MultipleFiltersChainWithoutMutatingInputAndDisposeAllOutputs()
    {
        var before = FrameHandle.ActiveBytes;
        using (var builder = new ImageFrameBuilder(11, 11, PixelFormat.Mono8))
        {
            builder.GetRowSpan(5)[5] = 255;
            using var image = builder.Build();
            using var first = GaussianBlurProcessor.Process(image, new GaussianBlurOptions { KernelSize = 3, SigmaX = 1 });
            using var second = GaussianBlurProcessor.Process(first, new GaussianBlurOptions { KernelSize = 3, SigmaX = 1 });
            using var p = first.Lease(); using var q = second.Lease();
            Assert.True(q.GetRowSpan(5)[5] < p.GetRowSpan(5)[5]);
            Assert.NotSame(first.BufferHandle, second.BufferHandle);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => GaussianBlurProcessor.Process(image, new GaussianBlurOptions(), ct: cancelled.Token));
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }
}

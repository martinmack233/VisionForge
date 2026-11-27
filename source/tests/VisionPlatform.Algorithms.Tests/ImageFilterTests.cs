using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace VisionPlatform.Algorithms.Tests;

public sealed class ImageFilterTests(ITestOutputHelper log)
{
    [Fact]
    public void ImpulseMeanAndExactSumHaveSeparateOutputs()
    {
        using var builder = new ImageFrameBuilder(9, 9, PixelFormat.Mono8);
        builder.GetRowSpan(4)[4] = 255;
        using var input = builder.Build();
        var p = new ImageFilterOptions { KernelWidth = 5, KernelHeight = 3 };
        using var mean = ImageFilterProcessor.Process(input, p);
        using var sum = ImageFilterProcessor.Sum(input, p with { Method = ImageFilterMethod.BoxSum });
        using var pixels = mean.Lease();
        using var sums = sum.Lease();
        Assert.Equal(new byte[] { 0, 0, 17, 17, 17, 17, 17, 0, 0 }, pixels.GetRowSpan(4).ToArray());
        Assert.Equal(0, pixels.GetRowSpan(2)[4]);
        Assert.Equal(255, MemoryMarshal.Cast<byte, float>(sums.GetRowSpan(4))[4]);
        Assert.Equal(input.FrameId, sum.FrameId);
        Assert.NotSame(input.BufferHandle, mean.BufferHandle);
        using var constant = new ImageFrameBuilder(7, 8, PixelFormat.Mono8);
        for (var y = 0; y < 8; y++) constant.GetRowSpan(y).Fill(255);
        using var full = constant.Build();
        using var high = ImageFilterProcessor.Sum(full, p with { Method = ImageFilterMethod.BoxSum, KernelWidth = 255, KernelHeight = 255 });
        using var words = high.Lease();
        Assert.Equal(16_581_375f, MemoryMarshal.Cast<byte, float>(words.GetRowSpan(0))[0]);
    }

    [Theory]
    [InlineData(ImageFilterMethod.Mean, PixelFormat.Mono8, GaussianBorderType.Constant)]
    [InlineData(ImageFilterMethod.Mean, PixelFormat.RGB8, GaussianBorderType.Reflect)]
    [InlineData(ImageFilterMethod.Mean, PixelFormat.BGR8, GaussianBorderType.Reflect101)]
    [InlineData(ImageFilterMethod.Gaussian, PixelFormat.Mono8, GaussianBorderType.Reflect101)]
    [InlineData(ImageFilterMethod.Gaussian, PixelFormat.BGR8, GaussianBorderType.Constant)]
    [InlineData(ImageFilterMethod.Median, PixelFormat.RGB8, GaussianBorderType.Constant)]
    [InlineData(ImageFilterMethod.Median, PixelFormat.Mono8, GaussianBorderType.Replicate)]
    [InlineData(ImageFilterMethod.Bilateral, PixelFormat.Mono8, GaussianBorderType.Reflect101)]
    [InlineData(ImageFilterMethod.Bilateral, PixelFormat.BGR8, GaussianBorderType.Replicate)]
    public void TiledOutputMatchesSingleCallAcrossBothSeams(ImageFilterMethod method, PixelFormat format, GaussianBorderType border)
    {
        const int width = 1031;
        const int height = 521;
        using var builder = new ImageFrameBuilder(width, height, format, width * format.BytesPerPixel() + 23, new("source", 41, 17));
        var rng = new Random(1234);
        for (var row = 0; row < height; row++) rng.NextBytes(builder.GetRowSpan(row));
        using var frame = builder.Build();
        using var view = frame.CreateView(2, 3, width - 4, height - 6);
        var p = new ImageFilterOptions { Method = method, BorderType = border, KernelWidth = 7, KernelHeight = 3, KernelSize = 7 };
        var roi = new RectRoi(view.CoordinateSpace.OriginX, view.CoordinateSpace.OriginY, view.Width, view.Height).ToRotated("source");
        using var result = ImageFilterProcessor.Process(frame, p, roi);
        Assert.Equal(view.CoordinateSpace, result.CoordinateSpace);
        Assert.Equal(format, result.PixelFormat);
        Assert.Equal(frame.Timestamp, result.Timestamp);
        using var source = new OpenCvImageLease(view);
        using var reference = new Mat();
        var cvBorder = (BorderTypes)Enum.Parse(typeof(BorderTypes), border.ToString()) | BorderTypes.Isolated;
        switch (method)
        {
            case ImageFilterMethod.Mean: Cv2.Blur(source.Mat, reference, new Size(7, 3), borderType: cvBorder); break;
            case ImageFilterMethod.Gaussian: Cv2.GaussianBlur(source.Mat, reference, new Size(7, 7), 1.2, borderType: cvBorder); break;
            case ImageFilterMethod.Median: Cv2.MedianBlur(source.Mat, reference, 7); break;
            case ImageFilterMethod.Bilateral: Cv2.BilateralFilter(source.Mat, reference, 5, 25, 5, cvBorder); break;
        }
        var error = MaxDifference(reference, result);
        log.WriteLine($"{method} {format} {border}: max absolute error vs whole ROI = {error} intensity levels");
        Assert.Equal(0, error);
    }

    [Fact]
    public void DirectionIsTheSamplingAxisAndBilateralRetainsTheStep()
    {
        using var builder = new ImageFrameBuilder(71, 31, PixelFormat.Mono8);
        for (var y = 0; y < 31; y++)
        for (var x = 0; x < 71; x++) builder.GetRowSpan(y)[x] = (byte)((x % 2 == 0 ? 0 : 18) + (x < 35 ? 45 : 175));
        using var input = builder.Build();
        var p = new ImageFilterOptions { Method = ImageFilterMethod.Directional, KernelSize = 7 };
        using var horizontal = ImageFilterProcessor.Process(input, p);
        using var vertical = ImageFilterProcessor.Process(input, p with { SamplingDirection = FilterSamplingDirection.Vertical });
        using var bilateral = ImageFilterProcessor.Process(input, p with { Method = ImageFilterMethod.Bilateral, Diameter = 7 });
        using var a = horizontal.Lease();
        using var b = vertical.Lease();
        using var c = bilateral.Lease();
        Assert.Equal(18, b.GetRowSpan(15)[11] - b.GetRowSpan(15)[10]);
        Assert.InRange(Math.Abs(a.GetRowSpan(15)[11] - a.GetRowSpan(15)[10]), 1, 3);
        var bilateralStep = c.GetRowSpan(15)[35] - c.GetRowSpan(15)[34];
        var horizontalStep = a.GetRowSpan(15)[35] - a.GetRowSpan(15)[34];
        Assert.InRange(bilateralStep, 125, 150);
        Assert.InRange(horizontalStep, 10, 35);
        log.WriteLine($"Known stripe contrast: vertical 18, horizontal {Math.Abs(a.GetRowSpan(15)[11] - a.GetRowSpan(15)[10])}. Edge step: bilateral {bilateralStep}, horizontal {horizontalStep}.");
    }

    [Fact]
    public void IsolatedRoiFormatsAndBadParametersAreChecked()
    {
        using var builder = new ImageFrameBuilder(9, 9, PixelFormat.BGR8);
        for (var y = 0; y < 9; y++) builder.GetRowSpan(y).Fill(255);
        for (var y = 3; y < 6; y++) builder.GetRowSpan(y).Slice(9, 9).Fill(90);
        using var input = builder.Build();
        using var bayer = new ImageFrameBuilder(9, 9, PixelFormat.BayerRG8).Build();
        var roi = new RectRoi(3, 3, 3, 3).ToRotated();
        using var filtered = ImageFilterProcessor.Process(input, new(), roi);
        using var filteredPixels = filtered.Lease();
        using var inputPixels = input.Lease();
        Assert.All(filteredPixels.GetRowSpan(0).ToArray(), p => Assert.Equal(90, p));
        Assert.Equal(255, inputPixels.GetRowSpan(2)[8]);
        Assert.Equal(3, filtered.CoordinateSpace.OriginX);
        Assert.Throws<NotSupportedException>(() => ImageFilterProcessor.Process(bayer, new()));
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Process(input, new() { KernelWidth = 2 }));
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Process(input, new(), roi with { AngleDegrees = 30 }));
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Process(input, new(), roi with { CoordinateSpaceId = "other" }));
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Process(input, new(), roi with { CenterX = 4.25 }));
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Sum(input, new() { Method = ImageFilterMethod.BoxSum }));
    }

    [Fact]
    public async Task LargeFrameRoiHasBoundedOwnedBuffersAndCancellationReleasesTheOutput()
    {
        var initial = FrameHandle.ActiveBytes;
        using var frame = LargeFrame();
        Assert.Equal(initial + 81_920_000, FrameHandle.ActiveBytes);
        var p = new ImageFilterOptions { Method = ImageFilterMethod.Bilateral };
        Assert.Throws<ArgumentException>(() => ImageFilterProcessor.Process(frame, p));
        var roi = new RectRoi(2048, 4096, 2048, 1536).ToRotated();
        for (var i = 0; i < 4; i++)
        {
            var elapsed = Stopwatch.StartNew();
            using var output = ImageFilterProcessor.Process(frame, p, roi);
            elapsed.Stop();
            Assert.Equal(initial + 81_920_000 + 3_145_728, FrameHandle.ActiveBytes);
            using var pixels = output.Lease();
            Assert.InRange(pixels.GetRowSpan(512)[512], (byte)38, (byte)150);
            log.WriteLine($"8192×10000 Mono8, 2048×1536 bilateral d=5, run {i} = {elapsed.Elapsed.TotalMilliseconds:F3} ms; owned frames = {FrameHandle.ActiveBytes - initial} bytes.");
        }
        using var cancel = new CancellationTokenSource();
        var running = Task.Run(() =>
        {
            using var result = ImageFilterProcessor.Process(frame, new() { Method = ImageFilterMethod.Median, KernelSize = 31 }, ct: cancel.Token);
        });
        Assert.True(SpinWait.SpinUntil(() => FrameHandle.ActiveBytes > initial + 81_920_000 || running.IsCompleted, TimeSpan.FromSeconds(5)));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        Assert.Equal(initial + 81_920_000, FrameHandle.ActiveBytes);
        Assert.Equal(1, frame.BufferHandle.ReferenceCount);
        frame.Dispose();
        Assert.Equal(initial, FrameHandle.ActiveBytes);
    }

    private static ImageFrame LargeFrame()
    {
        using var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8);
        var rng = new Random(8301);
        for (var y = 4096; y < 5632; y++)
        {
            var row = builder.GetRowSpan(y).Slice(2048, 2048);
            for (var x = 0; x < row.Length; x++) row[x] = (byte)rng.Next(38, 151);
        }
        return builder.Build();
    }

    private static unsafe int MaxDifference(Mat reference, ImageFrame actual)
    {
        using var after = actual.Lease();
        var maximum = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            var row = after.GetRowSpan(y);
            var wanted = new ReadOnlySpan<byte>((void*)reference.Ptr(y), row.Length);
            for (var x = 0; x < row.Length; x++) maximum = Math.Max(maximum, Math.Abs(row[x] - wanted[x]));
        }
        return maximum;
    }
}

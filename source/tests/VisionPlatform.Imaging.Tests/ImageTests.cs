using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionPlatform.Imaging.Tests;

public class ImageTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void FileRoundTripPreservesPixelMeaning(PixelFormat format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vision-platform-{Guid.NewGuid()}.png");
        try
        {
            using var builder = new ImageFrameBuilder(7, 5, format);
            for (var y = 0; y < builder.Height; y++)
            {
                var row = builder.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = (byte)(x * 9 + y * 5);
            }
            using var frame = builder.Build();
            ImageFile.Save(frame, path);
            using var loaded = ImageFile.Load(path, format);
            using var a = frame.Lease(); using var b = loaded.Lease();
            Assert.Equal(format, loaded.PixelFormat);
            for (var y = 0; y < frame.Height; y++) Assert.Equal(a.GetRowSpan(y).ToArray(), b.GetRowSpan(y).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8, PixelFormat.RGB8)]
    [InlineData(PixelFormat.BayerRG8, PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerGB8, PixelFormat.RGB8)]
    [InlineData(PixelFormat.BayerGB8, PixelFormat.BGR8)]
    public void PhysicalBayerPatternHasCorrectRedBlueChannels(PixelFormat rawFormat, PixelFormat outputFormat)
    {
        using var raw = CreateBayer(rawFormat);
        using var before = raw.Lease(); var original = before.GetRowSpan(2).ToArray();
        using var conversion = PixelFormatConverter.Convert(raw, outputFormat);
        using var converted = conversion.Image.Lease();
        Assert.True(conversion.AllocatedNewBuffer);
        Assert.Equal(new byte[] { 220, 100, 30 }, AsRgb(converted.GetRowSpan(4).Slice(4 * 3, 3), outputFormat));
        Assert.Equal(original, before.GetRowSpan(2).ToArray());
        Assert.NotSame(raw.BufferHandle, conversion.Image.BufferHandle);
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8, 1, 0)]
    [InlineData(PixelFormat.BayerRG8, 0, 1)]
    [InlineData(PixelFormat.BayerRG8, 1, 1)]
    [InlineData(PixelFormat.BayerGB8, 1, 0)]
    [InlineData(PixelFormat.BayerGB8, 0, 1)]
    [InlineData(PixelFormat.BayerGB8, 1, 1)]
    public void OddOffsetBayerViewMaintainsMosaicPhase(PixelFormat format, int x, int y)
    {
        using var raw = CreateBayer(format);
        using var view = raw.CreateView(x, y, 8, 8);
        using var converted = PixelFormatConverter.Convert(view, PixelFormat.RGB8);
        using var pixels = converted.Image.Lease();
        Assert.Equal(new byte[] { 220, 100, 30 }, pixels.GetRowSpan(3).Slice(9, 3).ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.RGB8, PixelFormat.BGR8)]
    [InlineData(PixelFormat.BGR8, PixelFormat.RGB8)]
    public void RgbBgrConversionSwapsChannelsAndHonorsStride(PixelFormat source, PixelFormat target)
    {
        byte[] bytes = [15, 90, 240, 25, 100, 230, 99, 99, 35, 110, 220, 45, 120, 210];
        using var frame = FrameFactory.CopyFrom(bytes, 2, 2, source, 8);
        using var result = PixelFormatConverter.Convert(frame, target);
        using var p = result.Image.Lease();
        Assert.Equal(new byte[] { 240, 90, 15, 230, 100, 25 }, p.GetRowSpan(0).ToArray());
        Assert.Equal(new byte[] { 220, 110, 35, 210, 120, 45 }, p.GetRowSpan(1).ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.RGB8, 255, 0, 0, 76)]
    [InlineData(PixelFormat.BGR8, 0, 0, 255, 76)]
    [InlineData(PixelFormat.RGB8, 0, 255, 0, 150)]
    [InlineData(PixelFormat.BGR8, 255, 0, 0, 29)]
    public void ColorToMonoUsesDeclaredChannelOrder(PixelFormat source, byte c0, byte c1, byte c2, byte expected)
    {
        using var frame = FrameFactory.CopyFrom([c0, c1, c2], 1, 1, source);
        using var result = PixelFormatConverter.Convert(frame, PixelFormat.Mono8);
        using var p = result.Image.Lease();
        Assert.Equal(expected, p.GetRowSpan(0)[0]);
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void MonoToColorReplicatesIntensity(PixelFormat target)
    {
        using var frame = FrameFactory.CopyFrom([17, 219], 2, 1, PixelFormat.Mono8);
        using var result = PixelFormatConverter.Convert(frame, target);
        using var p = result.Image.Lease();
        Assert.Equal(new byte[] { 17, 17, 17, 219, 219, 219 }, p.GetRowSpan(0).ToArray());
    }

    [Fact]
    public void StrideAndBufferValidationRejectUnsafeDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrameBuilder(3, 2, PixelFormat.RGB8, 8));
        Assert.Throws<ArgumentException>(() => FrameFactory.CopyFrom(new byte[10], 3, 2, PixelFormat.RGB8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrameBuilder(0, 2, PixelFormat.Mono8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrameBuilder(3, 2, (PixelFormat)100));
        Assert.Throws<OverflowException>(() => new ImageFrameBuilder(int.MaxValue, 2, PixelFormat.RGB8));
    }

    [Fact]
    public void ViewsShareAllocationPreserveCoordinatesAndOutliveParent()
    {
        var bytesBefore = FrameHandle.ActiveBytes;
        var frame = FrameFactory.CopyFrom(Enumerable.Range(0, 30).Select(x => (byte)x).ToArray(), 6, 5,
            PixelFormat.Mono8, coordinateSpace: new ImageCoordinateSpace("camera", 40, 90));
        var handle = frame.BufferHandle;
        using var view = frame.CreateView(2, 1, 3, 2);
        using var nested = view.CreateView(1, 1, 2, 1);
        using var share = frame.Share();
        Assert.Same(handle, view.BufferHandle);
        Assert.Equal(43, nested.CoordinateSpace.OriginX);
        Assert.Equal(92, nested.CoordinateSpace.OriginY);
        frame.Dispose(); frame.Dispose();
        using (var p = nested.Lease()) Assert.Equal(new byte[] { 15, 16 }, p.GetRowSpan(0).ToArray());
        Assert.Equal(bytesBefore + 30, FrameHandle.ActiveBytes);
        share.Dispose(); view.Dispose(); nested.Dispose();
        Assert.Equal(bytesBefore, FrameHandle.ActiveBytes);
    }

    [Fact]
    public void LeaseKeepsBufferAliveAndDisposesDeterministically()
    {
        var before = FrameHandle.ActiveBytes;
        using var builder = new ImageFrameBuilder(8, 4, PixelFormat.Mono8);
        builder.GetRowSpan(0)[0] = 42;
        var frame = builder.Build();
        Assert.Throws<ObjectDisposedException>(() => builder.GetRowSpan(0).ToArray());
        var lease = frame.Lease();
        frame.Dispose();
        Assert.Equal(42, lease.GetRowSpan(0)[0]);
        Assert.Throws<ObjectDisposedException>(() => frame.Lease());
        lease.Dispose(); lease.Dispose();
        Assert.Equal(before, FrameHandle.ActiveBytes);
        Assert.Throws<ObjectDisposedException>(() => lease.GetRowSpan(0).ToArray());
    }

    [Fact]
    public void RoiBoundsAreStrictAndNeverFallbackToWholeImage()
    {
        using var frame = new ImageFrameBuilder(8, 8, PixelFormat.Mono8).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.CreateView(-1, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.CreateView(7, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.CreateView(0, 0, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.CreateView(int.MaxValue, 0, int.MaxValue, 2));
    }

    [Fact]
    public void SameFormatConversionSharesWithoutCopying()
    {
        using var frame = new ImageFrameBuilder(8, 8, PixelFormat.Mono8).Build();
        using var result = PixelFormatConverter.Convert(frame, PixelFormat.Mono8);
        Assert.False(result.AllocatedNewBuffer);
        Assert.Same(frame.BufferHandle, result.Image.BufferHandle);
    }

    [Fact]
    public void InvalidConversionReleasesAnyTemporaryAllocation()
    {
        using var frame = new ImageFrameBuilder(8, 8, PixelFormat.BayerRG8).Build();
        var before = FrameHandle.ActiveBytes;
        Assert.Throws<NotSupportedException>(() => PixelFormatConverter.Convert(frame, PixelFormat.Mono8));
        Assert.Throws<NotSupportedException>(() => PixelFormatConverter.Convert(frame, PixelFormat.BayerGB8));
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public void LargeMonoImageLoadsAndRoiAddsNoImageAllocation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vision-platform-large-{Guid.NewGuid()}.bmp");
        var before = FrameHandle.ActiveBytes;
        try
        {
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8))
            {
                for (var y = 4500; y < 4510; y++) builder.GetRowSpan(y).Slice(4000, 20).Fill(211);
                using var image = builder.Build();
                ImageFile.Save(image, path);
            }
            using var loaded = ImageFile.Load(path);
            Assert.Equal(8192, loaded.Width); Assert.Equal(10000, loaded.Height);
            var allocated = FrameHandle.ActiveBytes;
            using var roi = loaded.CreateView(3995, 4495, 32, 24);
            Assert.Equal(allocated, FrameHandle.ActiveBytes);
            Assert.Same(loaded.BufferHandle, roi.BufferHandle);
            using var p = roi.Lease();
            Assert.Equal(211, p.GetRowSpan(5)[5]);
            Assert.Equal(0, p.GetRowSpan(0)[0]);
            using var mat = new OpenCvImageLease(roi);
            Assert.Equal(8192, mat.Mat.Step());
            Assert.Equal(211, mat.Mat.At<byte>(5, 5));
        }
        finally { File.Delete(path); }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void BayerRawFileRoundTripRetainsMosaic(PixelFormat format)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vision-platform-bayer-{Guid.NewGuid()}.png");
        try
        {
            using var raw = CreateBayer(format); ImageFile.Save(raw, path);
            using var loaded = ImageFile.Load(path, format);
            using var a = raw.Lease(); using var b = loaded.Lease();
            for (var y = 0; y < raw.Height; y++) Assert.Equal(a.GetRowSpan(y).ToArray(), b.GetRowSpan(y).ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnsupportedFileDepthAndMissingResourcesAreExplicit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vision-platform-16bit-{Guid.NewGuid()}.png");
        try
        {
            using var mat = new Mat(4, 4, MatType.CV_16UC1, Scalar.All(1000));
            Cv2.ImWrite(path, mat);
            Assert.Throws<NotSupportedException>(() => ImageFile.Load(path));
            Assert.Throws<FileNotFoundException>(() => ImageFile.Load(path + ".missing"));
        }
        finally { File.Delete(path); }
    }

    private static byte[] AsRgb(ReadOnlySpan<byte> pixel, PixelFormat format) =>
        format == PixelFormat.RGB8 ? pixel.ToArray() : [pixel[2], pixel[1], pixel[0]];

    private static ImageFrame CreateBayer(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(12, 12, format);
        for (var y = 0; y < 12; y++)
            for (var x = 0; x < 12; x++)
                builder.GetRowSpan(y)[x] = format == PixelFormat.BayerRG8
                    ? (byte)((y % 2, x % 2) switch { (0, 0) => 220, (1, 1) => 30, _ => 100 })
                    : (byte)((y % 2, x % 2) switch { (1, 0) => 220, (0, 1) => 30, _ => 100 });
        return builder.Build();
    }
}

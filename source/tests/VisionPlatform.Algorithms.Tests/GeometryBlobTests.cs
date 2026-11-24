using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ROI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public class GeometryBlobTests
{
    [Fact]
    public void RoiFollowsTranslationRotationAndScaleRelativeToReference()
    {
        using var image = new ImageFrameBuilder(300, 300, PixelFormat.Mono8).Build();
        var roi = new RotatedRectRoi(70, 50, 20, 10, 10);
        var reference = new Pose2D(50, 50, 15, 2);
        var current = new Pose2D(150, 120, 105, 3);
        var result = RoiTransformer.Transform(roi, reference, current, image);
        Assert.Equal(150, result.CenterX, 8); Assert.Equal(150, result.CenterY, 8);
        Assert.Equal(30, result.Width); Assert.Equal(15, result.Height); Assert.Equal(100, result.AngleDegrees);
    }

    [Fact]
    public void RoiRejectsOutOfBoundsInvalidScaleAndCoordinateMismatch()
    {
        using var image = new ImageFrameBuilder(100, 100, PixelFormat.Mono8).Build();
        var roi = new RotatedRectRoi(50, 50, 20, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => RoiTransformer.Transform(roi, new Pose2D(50, 50), new Pose2D(95, 95, 45), image));
        Assert.Throws<ArgumentException>(() => RoiTransformer.Transform(roi, new Pose2D(50, 50, Scale: 0), new Pose2D(50, 50), image));
        Assert.Throws<ArgumentException>(() => RoiTransformer.Transform(roi, new Pose2D(50, 50), new Pose2D(50, 50, CoordinateSpaceId: "display"), image));
    }

    [Fact]
    public void BlobMaskExcludesObjectInsideBoundingBoxButOutsideRotatedRoi()
    {
        using var builder = new ImageFrameBuilder(100, 100, PixelFormat.Mono8);
        Paint(builder, 47, 47, 5, 5); // Inside rotated narrow band.
        Paint(builder, 32, 61, 4, 4); // Inside the bounding box, outside its actual rotated shape.
        using var image = builder.Build();
        var roi = new RotatedRectRoi(50, 50, 50, 14, 45);
        var result = BlobDetector.Detect(image, roi, new BlobOptions());
        Assert.Single(result.Items); Assert.Equal(25, result.Items[0].Area);
        Assert.Equal(49, result.Items[0].CenterX); Assert.Equal(49, result.Items[0].CenterY);
        var whole = BlobDetector.Detect(image, null, new BlobOptions());
        Assert.Equal(2, whole.Count);
    }

    [Fact]
    public void ActualPoseChangesBlobSearchAndRejectsOldLocation()
    {
        using var builder = new ImageFrameBuilder(200, 160, PixelFormat.Mono8);
        Paint(builder, 97, 77, 7, 7);
        using var image = builder.Build();
        var taught = new RotatedRectRoi(40, 40, 24, 24);
        var shifted = RoiTransformer.Transform(taught, new Pose2D(40, 40), new Pose2D(100, 80, 10, 1.1), image);
        Assert.Equal(0, BlobDetector.Detect(image, taught, new BlobOptions()).Count);
        var result = BlobDetector.Detect(image, shifted, new BlobOptions());
        Assert.Single(result.Items); Assert.Equal(100, result.Items[0].CenterX); Assert.Equal(80, result.Items[0].CenterY);
    }

    [Fact]
    public void BlobOutputsOriginalCoordinatesFromImageViewAndAreaFiltering()
    {
        using var builder = new ImageFrameBuilder(100, 100, PixelFormat.Mono8);
        Paint(builder, 40, 50, 6, 4); Paint(builder, 55, 55, 2, 2);
        using var image = builder.Build();
        using var view = image.CreateView(35, 45, 30, 25);
        var result = BlobDetector.Detect(view, null, new BlobOptions { MinArea = 5, MaxArea = 30 });
        var blob = Assert.Single(result.Items);
        Assert.Equal(24, blob.Area); Assert.Equal(42.5, blob.CenterX); Assert.Equal(51.5, blob.CenterY);
        Assert.Equal(new RectRoi(40, 50, 6, 4), blob.Bounds);
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void ColorBlobUsesDeclaredChannels(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(20, 20, format);
        for (var y = 6; y < 10; y++)
            for (var x = 7; x < 11; x++) builder.GetRowSpan(y)[x * 3 + (format == PixelFormat.RGB8 ? 0 : 2)] = 255;
        using var image = builder.Build();
        Assert.Single(BlobDetector.Detect(image, null, new BlobOptions { Threshold = 60 }).Items);
        Assert.Empty(BlobDetector.Detect(image, null, new BlobOptions { Threshold = 90 }).Items);
    }

    [Fact]
    public void DarkForegroundThresholdAndMaskDoNotAddOutsideBackground()
    {
        using var builder = new ImageFrameBuilder(30, 30, PixelFormat.Mono8);
        for (var y = 0; y < 30; y++) builder.GetRowSpan(y).Fill(240);
        for (var y = 12; y < 16; y++) builder.GetRowSpan(y).Slice(12, 4).Fill(20);
        using var image = builder.Build();
        var results = BlobDetector.Detect(image, new RotatedRectRoi(15, 15, 20, 15, 25), new BlobOptions { Threshold = 100, DarkForeground = true });
        Assert.Equal(16, Assert.Single(results.Items).Area);
    }

    [Fact]
    public void BlobErrorsAndCancellationKeepImageAllocationBalanced()
    {
        var before = FrameHandle.ActiveBytes;
        using (var image = new ImageFrameBuilder(30, 30, PixelFormat.Mono8).Build())
        {
            Assert.Throws<ArgumentException>(() => BlobDetector.Detect(image, null, new BlobOptions { MinArea = 10, MaxArea = 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => BlobDetector.Detect(image, new RotatedRectRoi(2, 2, 20, 20), new BlobOptions()));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => BlobDetector.Detect(image, null, new BlobOptions(), cancel.Token));
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    private static void Paint(ImageFrameBuilder builder, int x, int y, int width, int height)
    { for (var row = y; row < y + height; row++) builder.GetRowSpan(row).Slice(x, width).Fill(230); }
}

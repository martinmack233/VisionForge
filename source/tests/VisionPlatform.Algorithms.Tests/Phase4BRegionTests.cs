using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class Phase4BRegionTests
{
    [Fact]
    public void FractionalGlobalRoiOutwardRasterizationRetainsOriginStrideAndSharedOwnership()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var image = new ImageFrameBuilder(100, 80, PixelFormat.Mono8, 128, new("sensor", 1000, 2000)).Build())
        {
            using var view = RecognitionRegion.Open(image, new(1020.5, 2030.5, 10.5, 20.5, 0, "sensor"));
            Assert.Equal(128, view.Stride); Assert.Same(image.BufferHandle, view.BufferHandle);
            Assert.Equal(1015, view.CoordinateSpace.OriginX); Assert.Equal(2020, view.CoordinateSpace.OriginY);
            Assert.Equal(11, view.Width); Assert.Equal(21, view.Height);
            Assert.False(image.IsDisposed);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }
    [Fact]
    public void InvalidSpaceRotationBoundsAndPixelBudgetAreRejected()
    {
        using var image = new ImageFrameBuilder(100, 80, PixelFormat.Mono8).Build();
        Assert.Throws<ArgumentException>(() => RecognitionRegion.Open(image, new(20, 30, 10, 20, 5)));
        Assert.Throws<ArgumentException>(() => RecognitionRegion.Open(image, new(20, 30, 10, 20, 0, "other")));
        Assert.Throws<ArgumentException>(() => RecognitionRegion.Open(image, new(0, 0, 10, 20)));
        Assert.Throws<ArgumentException>(() => RecognitionRegion.Open(image, null, 7999));
        Assert.Throws<ArgumentException>(() => RecognitionRegion.Open(image, new(20, 30, 10, 20), 199));
    }
}

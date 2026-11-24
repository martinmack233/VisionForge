using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Results;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class CoordinateTransformContractTests
{
    [Fact]
    public void CompositionAndInversePreservePixelCenters()
    {
        var first = new CoordinateTransform2D(0, -1, 1, 0, 14, 30, "crop", "view");
        var second = new CoordinateTransform2D(2, 0, 0, 2, 100, 200, "view", "image");
        var point = new Point2D(1.25, 5.5);
        Assert.Equal(second.Map(first.Map(point)), first.Then(second).Map(point));
        Assert.Equal(point, first.Then(second).Inverse().Map(first.Then(second).Map(point)));
        Assert.Throws<ArgumentException>(() => second.Then(first));
        Assert.Throws<InvalidOperationException>(() => new CoordinateTransform2D(0, 0, 0, 0, 1, 1).Inverse());
    }

    [Fact]
    public void OriginalBlobConstructorAndAbsentGeometryAreCompatible()
    {
        var blob = new BlobResult(2, 15, 10, 20, new(8, 19, 5, 3));
        var results = new BlobResults([blob]);
        Assert.Equal(1, results.Count); Assert.Equal("image", results.CoordinateSpaceId);
        Assert.Null(blob.Circularity); Assert.Null(blob.PixelRuns);
        Assert.Equal(0, results.RasterWidth);
    }
}

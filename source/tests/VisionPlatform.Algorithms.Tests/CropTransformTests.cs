using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class CropTransformTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void AxisCropRespectsStrideAndOriginAndOwnsOnlyItsOutput(PixelFormat format)
    {
        var channels = format.BytesPerPixel();
        using var builder = new ImageFrameBuilder(9, 7, format, 9 * channels + 5, new ImageCoordinateSpace("sensor", 100, 200));
        for (var y = 0; y < 7; y++)
            for (var x = 0; x < 9; x++)
                for (var c = 0; c < channels; c++) builder.GetRowSpan(y)[x * channels + c] = (byte)(y * 20 + x + c * 3);
        using var input = builder.Build();
        var cropped = CropImageProcessor.Process(input, new(), new RectRoi(102, 203, 4, 3));
        using (cropped.Image)
        {
            Assert.Equal(format, cropped.Image.PixelFormat);
            Assert.NotSame(input.BufferHandle, cropped.Image.BufferHandle);
            Assert.Equal(0, cropped.Image.CoordinateSpace.OriginX);
            Assert.NotEqual(input.CoordinateSpace.Id, cropped.Image.CoordinateSpace.Id);
            Assert.Equal(new Point2D(102, 203), cropped.CoordinateTransform.Map(new Point2D(0, 0)));
            Assert.Equal(new Point2D(105, 205), cropped.CoordinateTransform.Map(new Point2D(3, 2)));
            using var pixels = cropped.Image.Lease();
            for (var y = 0; y < 3; y++)
                for (var x = 0; x < 4; x++)
                    for (var c = 0; c < channels; c++) Assert.Equal((byte)((y + 3) * 20 + x + 2 + c * 3), pixels.GetRowSpan(y)[x * channels + c]);
        }
        Assert.False(input.IsDisposed);
        using var original = input.Lease();
        Assert.Equal((byte)62, original.GetRowSpan(3)[2 * channels]);
    }

    [Theory]
    [InlineData(4, 6)]
    [InlineData(5, 7)]
    public void ZeroAngleExistingRoiMatchesOrdinaryCropWithoutHalfPixelShift(int width, int height)
    {
        using var builder = Gradient(20, 20);
        using var input = builder.Build();
        var rect = new RectRoi(4, 5, width, height);
        var ordinary = CropImageProcessor.Process(input, new(), rect);
        var rotated = RotatedCropProcessor.Process(input, new() { Interpolation = CropInterpolation.Nearest }, rect.ToRotated());
        using (ordinary.Image) using (rotated.Image)
        using (var a = ordinary.Image.Lease()) using (var b = rotated.Image.Lease())
        {
            for (var y = 0; y < height; y++) Assert.Equal(a.GetRowSpan(y).ToArray(), b.GetRowSpan(y).ToArray());
            AssertPoint(new(4, 5), rotated.CoordinateTransform.Map(new(0, 0)));
            AssertPoint(new(4 + width - 1, 5 + height - 1), rotated.CoordinateTransform.Map(new(width - 1, height - 1)));
        }
    }

    [Fact]
    public void ClockwiseQuarterTurnSamplesAroundCorrectPixelCenter()
    {
        using var builder = Gradient(9, 9);
        using var input = builder.Build();
        var result = RotatedCropProcessor.Process(input, new() { CenterX = 4, CenterY = 4, Width = 3, Height = 3,
            Angle = 90, Interpolation = CropInterpolation.Nearest });
        using (result.Image) using (var pixels = result.Image.Lease())
        {
            Assert.Equal((byte)35, pixels.GetRowSpan(0)[0]); // output top-left samples input (5,3)
            Assert.Equal((byte)44, pixels.GetRowSpan(1)[1]);
            Assert.Equal((byte)53, pixels.GetRowSpan(2)[2]);
            AssertPoint(new(5, 3), result.CoordinateTransform.Map(new(0, 0)));
            AssertPoint(new(3, 5), result.CoordinateTransform.Map(new(2, 2)));
        }
    }

    [Theory]
    [InlineData(13.7)]
    [InlineData(-37.2)]
    [InlineData(181.4)]
    public void ArbitraryAngleKeepsCenterAndHasInvertibleCoordinateMapping(double angle)
    {
        using var builder = Gradient(20, 20);
        using var input = builder.Build();
        var result = RotatedCropProcessor.Process(input, new() { CenterX = 8, CenterY = 9, Width = 7, Height = 5, Angle = angle });
        using (result.Image) using (var pixels = result.Image.Lease())
        {
            Assert.Equal((byte)98, pixels.GetRowSpan(2)[3]);
            AssertPoint(new(8, 9), result.CoordinateTransform.Map(new(3, 2)));
            var source = new Point2D(1.25, 3.75);
            AssertPoint(source, result.CoordinateTransform.Inverse().Map(result.CoordinateTransform.Map(source)));
        }
    }

    [Theory]
    [InlineData(CropBorderMode.Constant, 77)]
    [InlineData(CropBorderMode.Replicate, 0)]
    public void OutsideSamplesUseExplicitBorderAndDoNotClampCropGeometry(CropBorderMode borderMode, byte expected)
    {
        using var builder = Gradient(5, 5);
        using var input = builder.Build();
        var result = RotatedCropProcessor.Process(input, new() { CenterX = 0, CenterY = 0, Width = 3, Height = 3,
            BorderMode = borderMode, BorderValue = 77, Interpolation = CropInterpolation.Nearest });
        using (result.Image) using (var pixels = result.Image.Lease())
        {
            Assert.Equal(expected, pixels.GetRowSpan(0)[0]);
            Assert.Equal((byte)0, pixels.GetRowSpan(1)[1]);
            Assert.Equal((byte)11, pixels.GetRowSpan(2)[2]);
            AssertPoint(new(-1, -1), result.CoordinateTransform.Map(new(0, 0)));
        }
    }

    [Fact]
    public void PoseOverridesCenterAndAngleAndScalesDimensions()
    {
        using var builder = Gradient(25, 25);
        using var input = builder.Build();
        var pose = new Pose2D(13, 12, 90, 1.5);
        var result = RotatedCropProcessor.Process(input, new() { Width = 4, Height = 6, CenterX = -20, CenterY = -30, Angle = 17 }, pose: pose);
        using (result.Image)
        {
            Assert.Equal(6, result.Image.Width); Assert.Equal(9, result.Image.Height);
            AssertPoint(new(13, 12), result.CoordinateTransform.Map(new(2.5, 4)));
            AssertPoint(new(13, 13), result.CoordinateTransform.Map(new(3.5, 4)));
        }
    }

    [Fact]
    public void BlobAfterRotatedCropMapsBackToOriginalImage()
    {
        using var builder = new ImageFrameBuilder(100, 100, PixelFormat.Mono8);
        for (var y = 42; y < 47; y++) builder.GetRowSpan(y).Slice(56, 5).Fill(240);
        using var input = builder.Build();
        var result = RotatedCropProcessor.Process(input, new() { CenterX = 50, CenterY = 50, Width = 31, Height = 31,
            Angle = 90, Interpolation = CropInterpolation.Nearest });
        using (result.Image)
        {
            var detected = BlobDetector.Detect(result.Image, null, new());
            var blob = Assert.Single(detected.Items);
            Assert.Equal(25, blob.Area);
            AssertPoint(new(58, 44), result.CoordinateTransform.Map(new(blob.CenterX, blob.CenterY)));
        }
    }

    [Fact]
    public void NestedCropTransformsComposeWithoutAddingSourceOriginTwice()
    {
        using var builder = Gradient(30, 30);
        using var input = builder.Build();
        using var view = input.CreateView(5, 7, 20, 20);
        var first = CropImageProcessor.Process(view, new(), new RectRoi(8, 10, 10, 10));
        using (first.Image)
        {
            var second = CropImageProcessor.Process(first.Image, new() { X = 2, Y = 3, Width = 3, Height = 3 });
            using (second.Image)
            {
                var combined = second.CoordinateTransform.Then(first.CoordinateTransform);
                AssertPoint(new(10, 13), combined.Map(new(0, 0)));
                AssertPoint(new(12, 15), combined.Map(new(2, 2)));
            }
        }
    }

    [Fact]
    public void InvalidBoundsGeometryEnumsAndCancellationReleaseBuffers()
    {
        var before = FrameHandle.ActiveBytes;
        using (var input = new ImageFrameBuilder(15, 15, PixelFormat.Mono8).Build())
        {
            var baseline = FrameHandle.ActiveBytes;
            Assert.Throws<ArgumentOutOfRangeException>(() => CropImageProcessor.Process(input, new() { X = 14, Width = 3, Height = 2 }));
            Assert.Throws<ArgumentException>(() => CropImageProcessor.Process(input, new(), new RectRoi(.5, 0, 2, 2)));
            Assert.Throws<ArgumentException>(() => CropImageProcessor.Process(input, new() { Width = 0 }));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new() { Angle = double.NaN }));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new() { Interpolation = (CropInterpolation)999 }));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new() { BorderMode = (CropBorderMode)999 }));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new(), pose: new(1, 1, Scale: 0)));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new(), roi: new(5, 5, 3, 3), pose: new(5, 5)));
            Assert.Throws<ArgumentException>(() => RotatedCropProcessor.Process(input, new(), roi: new(5, 5, 3, 3, CoordinateSpaceId: "display")));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => CropImageProcessor.Process(input, new(), ct: cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => RotatedCropProcessor.Process(input, new(), ct: cancellation.Token));
            Assert.Equal(baseline, FrameHandle.ActiveBytes);
            Assert.False(input.IsDisposed);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void BayerRequiresExplicitConversion(PixelFormat format)
    {
        using var image = new ImageFrameBuilder(10, 10, format).Build();
        Assert.Throws<NotSupportedException>(() => CropImageProcessor.Process(image, new() { Width = 5, Height = 5 }));
        Assert.Throws<NotSupportedException>(() => RotatedCropProcessor.Process(image, new() { Width = 5, Height = 5 }));
    }

    [Fact]
    public async Task RegisteredCropNodesRunAndReportFailuresThroughProductionRuntime()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var builder = Gradient(30, 30))
        using (var input = builder.Build())
        {
            var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
            var spec = new NodeSpecification("crop", "CropImage", "普通裁切", JsonDefaults.Element(new CropImageOptions { X = 4, Y = 5, Width = 8, Height = 7 }));
            using (var run = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = input }))
            {
                Assert.True(run.Success, run.ErrorMessage);
                Assert.Equal(8, run.Nodes["crop"].Output<ImageFrame>("CroppedImage").Width);
                AssertPoint(new(4, 5), run.Nodes["crop"].Output<CoordinateTransform2D>("CoordinateTransform").Map(new(0, 0)));
            }
            spec = new NodeSpecification("rotated", "RotatedCrop", "旋转裁切", JsonDefaults.Element(new RotatedCropOptions { Width = 5, Height = 5 }));
            using (var run = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = input, ["Pose2D"] = new Pose2D(10, 11, 90) }))
            {
                Assert.True(run.Success, run.ErrorMessage);
                AssertPoint(new(10, 11), run.Nodes["rotated"].Output<CoordinateTransform2D>("CoordinateTransform").Map(new(2, 2)));
            }
            using (var failed = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = input, ["Pose2D"] = new Pose2D(10, 11, Scale: -1) }))
            {
                Assert.Equal(NodeExecutionStatus.Failed, failed.Nodes["rotated"].Status);
                Assert.Contains("scale", failed.Nodes["rotated"].ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(failed.Nodes["rotated"].Outputs);
            }
            Assert.False(input.IsDisposed);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public void CoordinateTransformRejectsSingularInverseAndMismatchedComposition()
    {
        Assert.Throws<InvalidOperationException>(() => new CoordinateTransform2D(1, 2, 2, 4, 0, 0).Inverse());
        Assert.Throws<ArgumentException>(() => new CoordinateTransform2D(1, 0, 0, 1, 0, 0, "a", "b").Then(new(1, 0, 0, 1, 0, 0, "other", "z")));
    }

    private static ImageFrameBuilder Gradient(int width, int height)
    {
        var builder = new ImageFrameBuilder(width, height, PixelFormat.Mono8);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) builder.GetRowSpan(y)[x] = (byte)(y * 10 + x);
        return builder;
    }

    private static void AssertPoint(Point2D expected, Point2D actual)
    { Assert.Equal(expected.X, actual.X, 8); Assert.Equal(expected.Y, actual.Y, 8); }
}

using OpenCvSharp;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase3CTransformTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(180)]
    public void RotationExpandedQuarterTurnsMatchKnownPixelsAndInverseMapping(double angle)
    {
        using var input = Image(3, 2);
        var result = RotateImageProcessor.Process(input, new() { Angle = angle, OutputSize = RotationOutputSize.ExpandToFit, Interpolation = CropInterpolation.Nearest });
        using (result.Image) using (var source = input.Lease()) using (var pixels = result.Image.Lease())
        {
            Assert.Equal(Math.Abs(angle) == 90 ? 2 : 3, result.Image.Width);
            Assert.Equal(Math.Abs(angle) == 90 ? 3 : 2, result.Image.Height);
            byte[][] expected = angle switch
            {
                90 => [[10, 0], [11, 1], [12, 2]],
                -90 => [[2, 12], [1, 11], [0, 10]],
                _ => [[12, 11, 10], [2, 1, 0]]
            };
            for (var y = 0; y < result.Image.Height; y++)
            {
                Assert.Equal(expected[y], pixels.GetRowSpan(y).ToArray());
                for (var x = 0; x < result.Image.Width; x++)
                {
                    var p = result.CoordinateTransform.Map(new(x, y));
                    Assert.Equal(source.GetRowSpan((int)Math.Round(p.Y))[(int)Math.Round(p.X)], pixels.GetRowSpan(y)[x]);
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13.7)]
    [InlineData(-41.2)]
    [InlineData(360000013.7)]
    public void RotationAroundSpecifiedPixelCenterRetainsTheCenterAndHasCorrectClockwiseOrientation(double angle)
    {
        using var input = Image(11, 9, origin: new("sensor", 100, 200));
        var result = RotateImageProcessor.Process(input, new() { Angle = angle, CenterX = 103, CenterY = 204 });
        using (result.Image) using (var pixels = result.Image.Lease())
        {
            Assert.Equal((byte)43, pixels.GetRowSpan(4)[3]);
            Point(new(103, 204), result.CoordinateTransform.Map(new(3, 4)));
            var output = result.CoordinateTransform.Inverse().Map(new(104, 204));
            var radians = (angle % 360) * Math.PI / 180;
            Point(new(3 + Math.Cos(radians), 4 + Math.Sin(radians)), output);
            Assert.Equal(0, result.Image.CoordinateSpace.OriginX);
            Assert.NotEqual(input.CoordinateSpace.Id, result.Image.CoordinateSpace.Id);
        }
    }

    [Theory]
    [InlineData(CropBorderMode.Constant)]
    [InlineData(CropBorderMode.Replicate)]
    [InlineData(CropBorderMode.Reflect)]
    [InlineData(CropBorderMode.Reflect101)]
    [InlineData(CropBorderMode.Wrap)]
    public void RotationBordersAndSamplingAgreeWithIndependentNativeForwardMatrix(CropBorderMode border)
    {
        using var input = Image(13, 9);
        var options = new RotateImageOptions { Angle = 31.5, CenterX = 2, CenterY = 3, BorderMode = border, BorderValue = 77, Interpolation = CropInterpolation.Linear };
        var result = RotateImageProcessor.Process(input, options);
        using (result.Image) using (var image = new OpenCvImageLease(input)) using (var reference = new Mat())
        using (var forward = Cv2.GetRotationMatrix2D(new Point2f(2, 3), -31.5, 1))
        {
            var nativeBorder = border switch { CropBorderMode.Constant => BorderTypes.Constant, CropBorderMode.Replicate => BorderTypes.Replicate,
                CropBorderMode.Reflect => BorderTypes.Reflect, CropBorderMode.Reflect101 => BorderTypes.Reflect101, _ => BorderTypes.Wrap };
            Cv2.WarpAffine(image.Mat, reference, forward, new Size(13, 9), InterpolationFlags.Linear, nativeBorder, Scalar.All(77));
            using var actual = new OpenCvImageLease(result.Image);
            Assert.Equal(0, Cv2.Norm(reference, actual.Mat, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void ExpandedArbitraryRotationPreservesFormatAndEnclosesEveryPixelBoundary(PixelFormat format)
    {
        using var input = Image(11, 8, format, new("view", 50, 70));
        var original = Bytes(input); var result = RotateImageProcessor.Process(input, new() { Angle = 27.3, OutputSize = RotationOutputSize.ExpandToFit });
        using (result.Image)
        {
            Assert.Equal(format, result.Image.PixelFormat); Assert.NotSame(input.BufferHandle, result.Image.BufferHandle);
            var inverse = result.CoordinateTransform.Inverse();
            foreach (var corner in new[] { new Point2D(49.5, 69.5), new(60.5, 69.5), new(60.5, 77.5), new(49.5, 77.5) })
            {
                var mapped = inverse.Map(corner);
                Assert.InRange(mapped.X, -.500000001, result.Image.Width - .5 + 1e-9);
                Assert.InRange(mapped.Y, -.500000001, result.Image.Height - .5 + 1e-9);
            }
        }
        Assert.Equal(original, Bytes(input));
    }

    [Theory]
    [InlineData(ResizeInterpolation.Nearest)]
    [InlineData(ResizeInterpolation.Linear)]
    [InlineData(ResizeInterpolation.Cubic)]
    [InlineData(ResizeInterpolation.Area)]
    [InlineData(ResizeInterpolation.Lanczos4)]
    public void ResizeModesMapActualRoundedDimensionsWithHalfPixelCenters(ResizeInterpolation interpolation)
    {
        using var input = Image(7, 5, origin: new("sensor", 100, 200));
        var result = ResizeImageProcessor.Process(input, new() { Mode = ResizeImageMode.Scale, ScaleX = 1.5, ScaleY = .5, Interpolation = interpolation });
        using (result.Image)
        {
            Assert.Equal(11, result.Image.Width); Assert.Equal(3, result.Image.Height);
            Point(new(100 + 3.5 / 11 - .5, 200 + 2.5 / 3 - .5), result.CoordinateTransform.Map(new(0, 0)));
            Point(new(103, 202), result.CoordinateTransform.Map(new(5, 1)));
            using var nativeInput = new OpenCvImageLease(input); using var reference = new Mat();
            var nativeInterpolation = interpolation switch { ResizeInterpolation.Nearest => (InterpolationFlags)6, ResizeInterpolation.Linear => InterpolationFlags.Linear,
                ResizeInterpolation.Cubic => InterpolationFlags.Cubic, ResizeInterpolation.Area => InterpolationFlags.Area, _ => InterpolationFlags.Lanczos4 };
            Cv2.Resize(nativeInput.Mat, reference, new Size(11, 3), 0, 0, nativeInterpolation);
            using var actual = new OpenCvImageLease(result.Image); Assert.Equal(0, Cv2.Norm(reference, actual.Mat, NormTypes.INF));
        }
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void ResizeIndependentOutputHandlesStrideViewOriginAndChannels(PixelFormat format)
    {
        using var input = Image(9, 8, format, new("sensor", 100, 200)); using var view = input.CreateView(2, 3, 5, 4);
        var result = ResizeImageProcessor.Process(view, new() { Width = 10, Height = 8, Interpolation = ResizeInterpolation.Nearest });
        using (result.Image) using (var src = view.Lease()) using (var output = result.Image.Lease())
        {
            Assert.Equal(format, result.Image.PixelFormat); Assert.NotSame(view.BufferHandle, result.Image.BufferHandle);
            Point(new(101.75, 202.75), result.CoordinateTransform.Map(new(0, 0)));
            for (var y = 0; y < result.Image.Height; y++)
                for (var x = 0; x < result.Image.Width; x++)
                    for (var channel = 0; channel < format.BytesPerPixel(); channel++)
                        Assert.Equal(src.GetRowSpan(y / 2)[x / 2 * format.BytesPerPixel() + channel], output.GetRowSpan(y)[x * format.BytesPerPixel() + channel]);
        }
    }

    [Fact]
    public void NearestResizeActuallyUsesHalfPixelExactSamplingInsteadOfLegacyTopLeftSampling()
    {
        using var builder = new ImageFrameBuilder(3, 1, PixelFormat.Mono8);
        builder.GetRowSpan(0)[0] = 10; builder.GetRowSpan(0)[1] = 100; builder.GetRowSpan(0)[2] = 240;
        using var input = builder.Build();
        var resized = ResizeImageProcessor.Process(input, new() { Width = 2, Height = 1, Interpolation = ResizeInterpolation.Nearest });
        using (resized.Image) using (var pixels = resized.Image.Lease())
        {
            // Half-pixel centers map to .25 and 1.75; old INTER_NEAREST instead picks source 0 and 1.
            Assert.Equal(new byte[] { 10, 240 }, pixels.GetRowSpan(0).ToArray());
            Point(new(.25, 0), resized.CoordinateTransform.Map(new(0, 0)));
            Point(new(1.75, 0), resized.CoordinateTransform.Map(new(1, 0)));
        }
    }

    [Theory]
    [InlineData(FlipDirection.Horizontal, PixelFormat.Mono8)]
    [InlineData(FlipDirection.Vertical, PixelFormat.Mono8)]
    [InlineData(FlipDirection.Both, PixelFormat.Mono8)]
    [InlineData(FlipDirection.Horizontal, PixelFormat.RGB8)]
    [InlineData(FlipDirection.Vertical, PixelFormat.BGR8)]
    [InlineData(FlipDirection.Both, PixelFormat.RGB8)]
    public void FlipUsesExactIndexMappingWithoutChangingInput(FlipDirection direction, PixelFormat format)
    {
        using var input = Image(9, 7, format, new("sensor", 100, 200)); using var view = input.CreateView(2, 1, 5, 4);
        var original = Bytes(input); var result = FlipImageProcessor.Process(view, new() { Direction = direction });
        using (result.Image) using (var source = view.Lease()) using (var actual = result.Image.Lease())
        {
            Assert.Equal(format, result.Image.PixelFormat);
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 5; x++)
                {
                    var xx = direction is FlipDirection.Horizontal or FlipDirection.Both ? 4 - x : x;
                    var yy = direction is FlipDirection.Vertical or FlipDirection.Both ? 3 - y : y;
                    Point(new(102 + xx, 201 + yy), result.CoordinateTransform.Map(new(x, y)));
                    for (var channel = 0; channel < format.BytesPerPixel(); channel++)
                        Assert.Equal(source.GetRowSpan(yy)[xx * format.BytesPerPixel() + channel], actual.GetRowSpan(y)[x * format.BytesPerPixel() + channel]);
                }
        }
        Assert.Equal(original, Bytes(input));
    }

    [Fact]
    public void TransformChainComposesBackToOriginalAndItsFramesHaveSeparateOwnership()
    {
        using var input = Image(9, 7, origin: new("sensor", 100, 200));
        var rotated = RotateImageProcessor.Process(input, new() { Angle = 90, OutputSize = RotationOutputSize.ExpandToFit });
        using (rotated.Image)
        {
            var resized = ResizeImageProcessor.Process(rotated.Image, new() { Width = 14, Height = 18 });
            using (resized.Image)
            {
                var flipped = FlipImageProcessor.Process(resized.Image, new() { Direction = FlipDirection.Both });
                using (flipped.Image)
                {
                    var composed = flipped.CoordinateTransform.Then(resized.CoordinateTransform).Then(rotated.CoordinateTransform);
                    var point = new Point2D(2.5, 3.25);
                    Point(rotated.CoordinateTransform.Map(resized.CoordinateTransform.Map(flipped.CoordinateTransform.Map(point))), composed.Map(point));
                    Point(point, composed.Inverse().Map(composed.Map(point)));
                    Assert.Equal("sensor", composed.TargetSpaceId); Assert.False(input.IsDisposed);
                }
            }
        }
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void BayerTransformsRequireExplicitConversion(PixelFormat format)
    {
        using var input = Image(8, 8, format);
        Assert.Throws<NotSupportedException>(() => RotateImageProcessor.Process(input, new()));
        Assert.Throws<NotSupportedException>(() => ResizeImageProcessor.Process(input, new()));
        Assert.Throws<NotSupportedException>(() => FlipImageProcessor.Process(input, new()));
    }

    [Fact]
    public void InvalidParametersAndPreCancellationReleaseAllNewResources()
    {
        var initial = FrameHandle.ActiveBytes;
        using (var input = Image(10, 10))
        {
            var baseline = FrameHandle.ActiveBytes;
            Assert.NotEmpty(RotateImageProcessor.Validate(new() { Angle = double.NaN }));
            Assert.NotEmpty(RotateImageProcessor.Validate(new() { CenterX = 1 }));
            Assert.NotEmpty(RotateImageProcessor.Validate(new() { CenterX = 1, CenterY = double.PositiveInfinity }));
            Assert.NotEmpty(RotateImageProcessor.Validate(new() { OutputSize = (RotationOutputSize)55, Interpolation = (CropInterpolation)55, BorderMode = (CropBorderMode)55 }));
            Assert.NotEmpty(ResizeImageProcessor.Validate(new() { Width = 0 }));
            Assert.NotEmpty(ResizeImageProcessor.Validate(new() { Mode = ResizeImageMode.Scale, ScaleX = double.NaN }));
            Assert.NotEmpty(ResizeImageProcessor.Validate(new() { Mode = (ResizeImageMode)55, Interpolation = (ResizeInterpolation)55 }));
            Assert.NotEmpty(FlipImageProcessor.Validate(new() { Direction = (FlipDirection)55 }));
            Assert.Throws<ArgumentException>(() => ResizeImageProcessor.Process(input, new() { Width = 32766, Height = 32766 }));
            Assert.Throws<ArgumentException>(() => ResizeImageProcessor.Process(input, new() { Mode = ResizeImageMode.Scale, ScaleX = double.MaxValue }));
            Assert.Throws<ArgumentException>(() => RotateImageProcessor.Process(input, new() { CenterX = double.MaxValue, CenterY = double.MaxValue, Angle = 90 }));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => RotateImageProcessor.Process(input, new(), cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => ResizeImageProcessor.Process(input, new(), cancelled.Token));
            Assert.Throws<OperationCanceledException>(() => FlipImageProcessor.Process(input, new(), cancelled.Token));
            Assert.Equal(baseline, FrameHandle.ActiveBytes); Assert.False(input.IsDisposed);
        }
        Assert.Equal(initial, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task RegisteredTransformNodesExecuteThroughSameRuntimeAndReportErrors()
    {
        var initial = FrameHandle.ActiveBytes;
        using (var input = Image(13, 9))
        {
            var registry = BuiltinRegistration.CreateRegistry();
            var specs = new[] { N("input", "ImageInput", new { }), N("rotate", "RotateImage", new RotateImageOptions { Angle = 90, OutputSize = RotationOutputSize.ExpandToFit }),
                N("resize", "ResizeImage", new ResizeImageOptions { Width = 18, Height = 26 }), N("flip", "FlipImage", new FlipImageOptions { Direction = FlipDirection.Both }) };
            // An explicit source node uses a shared input snapshot, exercising production node execution.
            using var inputRun = await new RuntimeExecutor(registry).ExecuteSingleAsync(specs[0], new Dictionary<string, object> { ["Image"] = input });
            using var first = await new RuntimeExecutor(registry).ExecuteSingleAsync(specs[1], new Dictionary<string, object> { ["Image"] = inputRun.Nodes["input"].Output<ImageFrame>("Image") });
            using var second = await new RuntimeExecutor(registry).ExecuteSingleAsync(specs[2], new Dictionary<string, object> { ["Image"] = first.Nodes["rotate"].Output<ImageFrame>("RotatedImage") });
            using var last = await new RuntimeExecutor(registry).ExecuteSingleAsync(specs[3], new Dictionary<string, object> { ["Image"] = second.Nodes["resize"].Output<ImageFrame>("ResizedImage") });
            Assert.True(first.Success); Assert.True(second.Success); Assert.True(last.Success);
            Assert.Equal(18, last.Nodes["flip"].Output<ImageFrame>("FlippedImage").Width);
            using var bad = await new RuntimeExecutor(registry).ExecuteSingleAsync(specs[1] with { Parameters = JsonDefaults.Element(new RotateImageOptions { Interpolation = (CropInterpolation)999 }) },
                new Dictionary<string, object> { ["Image"] = input });
            Assert.False(bad.Success); Assert.NotEmpty(bad.Nodes["rotate"].ErrorMessage!);
        }
        Assert.Equal(initial, FrameHandle.ActiveBytes);
    }

    private static ImageFrame Image(int width, int height, PixelFormat format = PixelFormat.Mono8, ImageCoordinateSpace? origin = null)
    {
        using var builder = new ImageFrameBuilder(width, height, format, width * format.BytesPerPixel() + 5, origin);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) for (var c = 0; c < format.BytesPerPixel(); c++)
            builder.GetRowSpan(y)[x * format.BytesPerPixel() + c] = (byte)(10 * y + x + 30 * c);
        return builder.Build();
    }
    private static byte[] Bytes(ImageFrame image)
    { using var lease = image.Lease(); return Enumerable.Range(0, image.Height).SelectMany(y => lease.GetRowSpan(y).ToArray()).ToArray(); }
    private static NodeSpecification N(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    private static void Point(Point2D expected, Point2D actual) { Assert.InRange(Math.Abs(expected.X - actual.X), 0, 1e-7); Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 1e-7); }
}

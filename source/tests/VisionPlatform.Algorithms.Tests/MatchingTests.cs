using OpenCvSharp;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Imaging;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class MatchingTests
{
    private readonly CvInspectTemplateMatcher _matcher = new();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-15, 1)]
    [InlineData(17.5, 1)]
    [InlineData(0, .9)]
    [InlineData(12.5, 1.1)]
    public void LocatesKnownSyntheticPoses(double angle, double scale)
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, angle, scale);
        var result = _matcher.Match(image, template, new TemplateMatchOptions());
        Assert.True(result.Success, result.FailureReason);
        Assert.InRange(Distance(result.CenterX - 221.25, result.CenterY - 163.75), 0, 1.25);
        Assert.InRange(Math.Abs(result.AngleDegrees - angle), 0, 1.5);
        // CvInspect refines angle and translation, but its scale stays on the coarse selected grid.
        Assert.InRange(Math.Abs(result.Scale - scale), 0, .061);
        Assert.NotNull(result.Pose);
        Assert.NotNull(result.MatchedRegion);
    }

    [Fact]
    public void FailureClearsPoseAndDoesNotReuseLastSuccess()
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, 0, 1);
        Assert.True(_matcher.Match(image, template, new()).Success);
        using var blank = FrameFactory.CopyFrom(new byte[384 * 320], 384, 320, PixelFormat.Mono8);
        var failure = _matcher.Match(blank, template, new());
        Assert.False(failure.Success);
        Assert.Null(failure.Pose);
        Assert.Null(failure.MatchedRegion);
        Assert.NotEmpty(failure.FailureReason!);
    }

    [Fact]
    public void SearchRoiAndImageViewUseOriginalCoordinates()
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, 12.5, 1);
        var options = new TemplateMatchOptions { SearchRoi = new(100, 70, 240, 210) };
        var full = _matcher.Match(image, template, options);
        using var view = image.CreateView(100, 70, 240, 210);
        var local = _matcher.Match(view, template, options);
        Assert.True(full.Success, full.FailureReason);
        Assert.True(local.Success, local.FailureReason);
        Assert.Equal(full.CenterX, local.CenterX, 6);
        Assert.Equal(full.CenterY, local.CenterY, 6);
        Assert.False(_matcher.Match(image, template, options with { SearchRoi = new(0, 0, 90, 90) }).Success);
        Assert.False(_matcher.Match(image, template, options with { SearchRoi = new(-1, 0, 90, 90) }).Success);
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void SupportsThreeProcessingFormatsAndRepeatableOutputs(PixelFormat format)
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, 0, 1);
        using var converted = PixelFormatConverter.Convert(image, format);
        var first = _matcher.Match(converted.Image, template, new());
        Assert.True(first.Success, first.FailureReason);
        for (var i = 0; i < 5; i++)
        {
            var next = _matcher.Match(converted.Image, template, new());
            Assert.Equal(first.Score, next.Score);
            Assert.Equal(first.CenterX, next.CenterX);
            Assert.Equal(first.CenterY, next.CenterY);
            Assert.Equal(first.AngleDegrees, next.AngleDegrees);
            Assert.Equal(first.Scale, next.Scale);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LowContrastAndNoiseAreMeasuredRatherThanHardcoded(bool noise)
    {
        using var template = CreateTemplate();
        using var source = CreateScene(template.Image, 0, 1);
        using var lease = new OpenCvImageLease(source);
        using var changed = new Mat();
        lease.Mat.ConvertTo(changed, MatType.CV_8UC1, noise ? 1 : .35, noise ? 0 : 60);
        if (noise)
        {
            var rng = new Random(20261030);
            var rows = changed.Rows; var cols = changed.Cols;
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < cols; x++)
                    changed.Set(y, x, (byte)Math.Clamp(changed.At<byte>(y, x) + rng.Next(-15, 16), 0, 255));
        }
        using var frame = FrameFactory.CopyFromMat(changed, PixelFormat.Mono8);
        var result = _matcher.Match(frame, template, new());
        Assert.True(result.Success, result.FailureReason);
        Assert.InRange(Distance(result.CenterX - 221.25, result.CenterY - 163.75), 0, 1.25);
    }

    [Fact]
    public void InvalidParametersAndBayerAreExplicitFailures()
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, 0, 1);
        Assert.False(_matcher.Match(image, template, new() { MinimumScore = double.NaN }).Success);
        Assert.False(_matcher.Match(image, template, new() { ScaleMin = .8, ScaleMax = 1.1 }).Success);
        using var bayer = FrameFactory.CopyFrom(new byte[100 * 100], 100, 100, PixelFormat.BayerRG8);
        Assert.Contains("Bayer", _matcher.Match(bayer, template, new()).FailureReason);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => _matcher.Match(image, template, new(), cancelled.Token));
    }

    [Fact]
    public void TemplateManifestRoundTripsAndProtectsResources()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-Matching-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Cleanup is deliberately scoped to files created in this unique test directory.
        try
        {
            using var template = CreateTemplate();
            var path = Path.Combine(directory, "template.json");
            var unrelated = Path.Combine(directory, "other.json");
            File.WriteAllText(unrelated, "preserve-me");
            TemplateResourceStore.Save(path, template);
            var manifest = File.ReadAllText(path);
            TemplateResourceStore.Save(path, template);
            Assert.Equal(manifest, File.ReadAllText(path));
            Assert.Equal("preserve-me", File.ReadAllText(unrelated));
            using var invalid = new TemplateModel(template.Image);
            invalid.Dispose();
            Assert.Throws<ObjectDisposedException>(() => TemplateResourceStore.Save(path, invalid));
            Assert.Equal(manifest, File.ReadAllText(path));
            using var loaded = TemplateResourceStore.Load(path);
            using var image = CreateScene(template.Image, 12.5, 1);
            var before = _matcher.Match(image, template, new());
            var after = _matcher.Match(image, loaded, new());
            Assert.True(after.Success, after.FailureReason);
            Assert.Equal(before.CenterX, after.CenterX);
            Assert.Equal(before.Score, after.Score);
            Assert.Equal(template.ReferenceX, loaded.ReferenceX);
            var asset = Directory.GetFiles(Path.Combine(directory, "template.templates"), "*.png").Single();
            File.AppendAllText(asset, "tamper");
            Assert.Throws<InvalidDataException>(() => TemplateResourceStore.Load(path));
            File.Delete(asset);
            Assert.Throws<FileNotFoundException>(() => TemplateResourceStore.Load(path));
            File.WriteAllText(path, manifest.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99"));
            Assert.Throws<InvalidDataException>(() => TemplateResourceStore.Load(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MatchingOnlyBorrowsItsInputs()
    {
        using var template = CreateTemplate();
        using var image = CreateScene(template.Image, 0, 1);
        var imageReferences = image.BufferHandle.ReferenceCount;
        var templateReferences = template.Image.BufferHandle.ReferenceCount;
        for (var i = 0; i < 8; i++) Assert.True(_matcher.Match(image, template, new()).Success);
        Assert.Equal(imageReferences, image.BufferHandle.ReferenceCount);
        Assert.Equal(templateReferences, template.Image.BufferHandle.ReferenceCount);
    }

    [Fact]
    public void CustomEditorReferenceDoesNotChangeGeometricCenter()
    {
        using var template = CreateTemplate();
        using var anchored = new TemplateModel(template.Image, "corner-anchor", 0, 0);
        using var image = CreateScene(template.Image, 12.5, 1);
        var center = _matcher.Match(image, template, new());
        var corner = _matcher.Match(image, anchored, new());
        Assert.True(corner.Success, corner.FailureReason);
        Assert.Equal(center.CenterX, corner.CenterX);
        Assert.Equal(center.CenterY, corner.CenterY);
        Assert.Equal(center.Pose, corner.Pose);
    }

    private static TemplateModel CreateTemplate()
    {
        using var mat = new Mat(64, 72, MatType.CV_8UC1);
        var rng = new Random(20261030);
        var rows = mat.Rows; var cols = mat.Cols;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++) mat.Set(y, x, (byte)rng.Next(70, 180));
        Cv2.GaussianBlur(mat, mat, new(3, 3), .7);
        Cv2.Rectangle(mat, new Rect(4, 4, 64, 56), new Scalar(210), 2);
        Cv2.Circle(mat, new Point(20, 21), 9, new Scalar(35), -1);
        Cv2.PutText(mat, "R", new Point(35, 45), HersheyFonts.HersheySimplex, .7, new Scalar(245), 2);
        Cv2.Line(mat, new Point(7, 53), new Point(29, 49), new Scalar(20), 3);
        using var frame = FrameFactory.CopyFromMat(mat, PixelFormat.Mono8);
        return new TemplateModel(frame, "synthetic-fixture");
    }

    private static ImageFrame CreateScene(ImageFrame template, double angle, double scale)
    {
        using var lease = new OpenCvImageLease(template);
        using var transform = new Mat(2, 3, MatType.CV_64FC1);
        var a = angle * Math.PI / 180;
        var c = scale * Math.Cos(a); var s = scale * Math.Sin(a);
        var cx = (template.Width - 1) / 2.0; var cy = (template.Height - 1) / 2.0;
        transform.Set(0, 0, c); transform.Set(0, 1, -s); transform.Set(0, 2, 221.25 - c * cx + s * cy);
        transform.Set(1, 0, s); transform.Set(1, 1, c); transform.Set(1, 2, 163.75 - s * cx - c * cy);
        using var scene = new Mat();
        Cv2.WarpAffine(lease.Mat, scene, transform, new Size(384, 320), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(45));
        return FrameFactory.CopyFromMat(scene, PixelFormat.Mono8);
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}

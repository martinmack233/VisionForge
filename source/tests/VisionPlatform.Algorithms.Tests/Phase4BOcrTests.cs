using System.Security.Cryptography;
using OpenCvSharp;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;
using Xunit.Abstractions;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase4BOcrTests(ITestOutputHelper log)
{
    private static string Models
    {
        get
        {
            for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            {
                var candidate = Path.Combine(path.FullName, "resources", "tessdata");
                if (File.Exists(Path.Combine(candidate, "eng.traineddata"))) return candidate;
            }
            return Path.Combine(AppContext.BaseDirectory, "tessdata");
        }
    }
    private static OcrOptions Options => new() { DataPath = Models, CharacterWhitelist = "0123456789" };

    [Fact]
    public void OfflineModelIsExactlyTheReviewedUpstreamResource()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Models, "eng.traineddata"));
        Assert.Equal(4_113_088, bytes.Length);
        Assert.Equal("7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void RealNativeOcrRecognizesDigitsWithRealConfidenceAndOriginalWordBoxes()
    {
        using var frame = TextImage("123456");
        var result = new TesseractOcrRecognizer().Recognize(frame, Options);
        log.WriteLine($"OCR text={result.Text}, confidence={result.Confidence:R}, elapsedMs={result.ExecutionTime.TotalMilliseconds:F3}");
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", result.Text);
        Assert.InRange(result.Confidence, .5, 1); Assert.True(result.ExecutionTime > TimeSpan.Zero);
        var word = Assert.Single(result.Words); Assert.Equal(result.Text, word.Text); Assert.InRange(word.Confidence, .5, 1);
        Assert.InRange(word.Bounds.X, 20, 45); Assert.InRange(word.Bounds.Y, 25, 65);
        Assert.InRange(word.Bounds.Width, 200, 500); Assert.True(word.Bounds.Height > 30);
        Assert.Equal("image", result.CoordinateSpaceId);
        Assert.Equal(RecognitionOverlayKind.Polygon, Assert.Single(result.Overlays).Kind);
    }

    [Fact]
    public void ActualEnglishLettersAreRecognizedWithoutCharacterWhitelist()
    {
        using var frame = TextImage("VISION");
        var result = new TesseractOcrRecognizer().Recognize(frame, Options with { CharacterWhitelist = "" });
        log.WriteLine($"English OCR: {result.Text}, confidence={result.Confidence:R}");
        Assert.True(result.Success, result.FailureReason); Assert.Equal("VISION", result.Text);
    }

    [Fact]
    public void RoiStrideAndFrameOriginDoNotModifySourceAndProduceOriginalCoordinates()
    {
        using var frame = TextImage("123456", new("sensor", 1000, 2000), paddedStride: true);
        using var beforeLease = frame.Lease(); var before = beforeLease.GetRowSpan(70).ToArray();
        var roi = new RotatedRectRoi(1300, 2070, 580, 130, CoordinateSpaceId: "sensor");
        var result = new TesseractOcrRecognizer().Recognize(frame, Options, roi);
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", result.Text);
        Assert.Equal("sensor", result.CoordinateSpaceId);
        var word = Assert.Single(result.Words);
        Assert.InRange(word.Bounds.X, 1020, 1045); Assert.InRange(word.Bounds.Y, 2025, 2065);
        Assert.All(result.Overlays, x => Assert.Equal("sensor", x.CoordinateSpaceId));
        using var afterLease = frame.Lease(); Assert.Equal(before, afterLease.GetRowSpan(70).ToArray());
        Assert.False(frame.IsDisposed);
    }

    [Fact]
    public void ExistingImageViewAddsOffsetOnceAndDoesNotDisposeBorrowedFrames()
    {
        using var full = TextImage("123456"); using var view = full.CreateView(10, 5, 580, 130);
        var count = full.BufferHandle.ReferenceCount;
        var result = new TesseractOcrRecognizer().Recognize(view, Options);
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", result.Text);
        Assert.InRange(Assert.Single(result.Words).Bounds.X, 20, 45);
        Assert.Equal(count, full.BufferHandle.ReferenceCount); Assert.False(view.IsDisposed);
    }

    [Fact]
    public void BlankImageReturnsCurrentEmptyResultAfterSuccessfulImage()
    {
        var engine = new TesseractOcrRecognizer(); using var text = TextImage("123456");
        Assert.True(engine.Recognize(text, Options).Success);
        using var blank = TextImage(""); var result = engine.Recognize(blank, Options);
        Assert.False(result.Success); Assert.Equal("", result.Text); Assert.Empty(result.Words); Assert.Empty(result.Overlays);
        Assert.Equal(0, result.Confidence); Assert.NotEmpty(result.FailureReason!);
    }

    [Theory]
    [InlineData(640, 160, 255)]
    [InlineData(600, 150, 255)]
    [InlineData(640, 160, 0)]
    [InlineData(640, 160, 127)]
    public void ConstantImagesContainNoTextRegardlessOfSegmentationHallucinations(int width, int height, byte value)
    {
        using var builder = new ImageFrameBuilder(width, height, PixelFormat.Mono8);
        for (var y = 0; y < height; y++) builder.GetRowSpan(y).Fill(value);
        using var image = builder.Build(); var result = new TesseractOcrRecognizer().Recognize(image, Options with { CharacterWhitelist = "" });
        log.WriteLine($"constant={value}, dimensions={width}x{height}, actualSuccess={result.Success}, text='{result.Text}', confidence={result.Confidence:R}");
        Assert.False(result.Success); Assert.Empty(result.Text); Assert.Empty(result.Words); Assert.Empty(result.Overlays); Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void RepeatedRealRecognitionHasConsistentValuesAndNoPlatformBufferReferenceLeak()
    {
        using var image = TextImage("123456"); var engine = new TesseractOcrRecognizer();
        var references = image.BufferHandle.ReferenceCount;
        var first = engine.Recognize(image, Options);
        for (var n = 0; n < 5; n++)
        {
            var next = engine.Recognize(image, Options);
            Assert.True(next.Success, next.FailureReason); Assert.Equal(first.Text, next.Text);
            Assert.Equal(first.Confidence, next.Confidence); Assert.Equal(first.Words.ToArray(), next.Words.ToArray());
            Assert.Equal(references, image.BufferHandle.ReferenceCount);
        }
    }

    [Fact]
    public void MissingLanguageModelReturnsExplicitFailureWithNoTextOrOverlay()
    {
        using var image = TextImage("123456");
        var result = new TesseractOcrRecognizer().Recognize(image, Options with { Language = "not_installed" });
        Assert.False(result.Success); Assert.Contains("model is missing", result.FailureReason);
        Assert.Empty(result.Text); Assert.Empty(result.Words); Assert.Empty(result.Overlays); Assert.False(image.IsDisposed);
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void NonMonoFormatsRequireExplicitConversion(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(20, 20, format); using var image = builder.Build();
        var ex = Assert.Throws<ArgumentException>(() => new TesseractOcrRecognizer().Recognize(image, Options));
        Assert.Contains("requires Mono8", ex.Message); Assert.False(image.IsDisposed);
    }

    [Fact]
    public void InvalidConfigurationRejectsBeforeNativeEngineAccess()
    {
        var invalid = new OcrOptions[]
        {
            new() { Language = "../eng" }, new() { Language = "" }, new() { MinimumConfidence = double.NaN },
            new() { MinimumConfidence = 1.1 }, new() { Segmentation = (OcrSegmentation)99 },
            new() { MaximumPixels = 0 }, new() { MaximumPixels = int.MaxValue }, new() { MaximumCharacters = 0 },
            new() { MaximumWords = 0 }, new() { CharacterWhitelist = "bad\n" },
            new() { UseRoi = true, RoiWidth = -1 }, new() { UseRoi = true, RoiY = double.PositiveInfinity }
        };
        using var image = TextImage("123456");
        foreach (var options in invalid)
        {
            Assert.NotEmpty(TesseractOcrRecognizer.Validate(options));
            Assert.Throws<ArgumentException>(() => new TesseractOcrRecognizer().Recognize(image, options));
        }
    }

    [Fact]
    public void RoiBoundsCoordinateSpaceRotationAndPixelBudgetAreStrict()
    {
        using var image = TextImage("123456"); var engine = new TesseractOcrRecognizer();
        Assert.Throws<ArgumentException>(() => engine.Recognize(image, Options, new(0, 0, 40, 40)));
        Assert.Throws<ArgumentException>(() => engine.Recognize(image, Options, new(300, 75, 40, 40, 10)));
        Assert.Throws<ArgumentException>(() => engine.Recognize(image, Options, new(300, 75, 40, 40, CoordinateSpaceId: "other")));
        Assert.Throws<ArgumentException>(() => engine.Recognize(image, Options with { MaximumPixels = 100 }));
        Assert.Equal(1, image.BufferHandle.ReferenceCount);
    }

    [Fact]
    public void OutputCharacterLimitReturnsFailureInsteadOfUnboundedOrPartialText()
    {
        using var image = TextImage("123456");
        var result = new TesseractOcrRecognizer().Recognize(image, Options with { MaximumCharacters = 3 });
        Assert.False(result.Success); Assert.Empty(result.Text); Assert.Empty(result.Words);
        Assert.Contains("MaximumCharacters", result.FailureReason);
    }

    [Theory]
    [InlineData(OcrSegmentation.SingleBlock)]
    [InlineData(OcrSegmentation.Auto)]
    public void ConfiguredSegmentationModesExecuteRealNativeRecognition(OcrSegmentation mode)
    {
        using var image = TextImage("123456");
        var result = new TesseractOcrRecognizer().Recognize(image, Options with { Segmentation = mode });
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", result.Text);
    }

    [Fact]
    public void TooManyWordsFailWithoutPublishingPartialResults()
    {
        using var image = TextImage("VISION 1234");
        var result = new TesseractOcrRecognizer().Recognize(image, Options with { CharacterWhitelist = "", MaximumWords = 1 });
        Assert.False(result.Success); Assert.Contains("MaximumWords", result.FailureReason);
        Assert.Empty(result.Text); Assert.Empty(result.Words); Assert.Empty(result.Overlays);
    }

    [Fact]
    public void RealConfidenceThresholdRejectsCurrentTextWithoutReturningItAsAccepted()
    {
        using var image = TextImage("123456");
        var engine = new TesseractOcrRecognizer(); var accepted = engine.Recognize(image, Options);
        Assert.True(accepted.Success); Assert.InRange(accepted.Confidence, .5, .98);
        var rejected = engine.Recognize(image, Options with { MinimumConfidence = .99 });
        Assert.False(rejected.Success); Assert.Empty(rejected.Text); Assert.Empty(rejected.Words); Assert.Empty(rejected.Overlays);
        Assert.Equal(accepted.Confidence, rejected.Confidence); Assert.Contains("below MinimumConfidence", rejected.FailureReason);
    }

    [Theory]
    [InlineData(180, 130, 0)]
    [InlineData(245, 10, 12)]
    public void DefinedSyntheticLowContrastAndNoiseFixturesRunActualRecognition(byte background, byte foreground, int noise)
    {
        using var image = TextImage("123456", background: background, foreground: foreground, noise: noise);
        var result = new TesseractOcrRecognizer().Recognize(image, Options);
        log.WriteLine($"contrast={background - foreground}, noiseAmplitude={noise}, text={result.Text}, confidence={result.Confidence:R}");
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", result.Text);
    }

    [Fact]
    public void PreCancellationDoesNotStartEngineOrRetainImage()
    {
        using var image = TextImage("123456"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new TesseractOcrRecognizer().Recognize(image, Options, cancellationToken: cancellation.Token));
        Assert.Equal(1, image.BufferHandle.ReferenceCount);
    }

    [Fact]
    public async Task ProductionRuntimeSingleNodeUsesRealOcrAndInputRoiOverridesParameterRoi()
    {
        using var image = TextImage("123456");
        var spec = new NodeSpecification("ocr", "OCR", "OCR", JsonDefaults.Element(Options with { UseRoi = true, RoiX = -999 }));
        var registry = new NodeRegistry(); registry.Register("OCR", () => new OcrNode());
        using var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(spec,
            new Dictionary<string, object> { ["Image"] = image, ["ROI"] = new RotatedRectRoi(300, 75, 590, 140) });
        Assert.True(run.Success, run.ErrorMessage);
        var node = run.Nodes["ocr"]; var result = node.Output<OcrResult>("Result");
        Assert.True(result.Success, result.FailureReason); Assert.Equal("123456", node.Output<string>("Text"));
        Assert.Equal(result.Confidence, node.Output<double>("Confidence")); Assert.True(node.Output<bool>("Success"));
        Assert.False(image.IsDisposed); Assert.Equal(1, image.BufferHandle.ReferenceCount);
    }

    [Fact]
    public async Task RelativeModelPathIsResolvedAgainstProjectResourceRoot()
    {
        using var image = TextImage("123456");
        var spec = new NodeSpecification("ocr", "OCR", "OCR", JsonDefaults.Element(Options with { DataPath = "tessdata" }));
        var node = new OcrNode(); var root = Directory.GetParent(Models)!.FullName;
        var output = await node.ExecuteAsync(new(spec, new Dictionary<string, object> { ["Image"] = image }, root), default);
        Assert.True(((OcrResult)output.Values["Result"]).Success); Assert.Empty(output.OwnedResources);
    }

    [Fact]
    public async Task RealImageInputOcrAndTextDisplayDagMatchesDirectHeadlessResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-Ocr-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var image = TextImage("123456"); ImageFile.Save(image, Path.Combine(directory, "digits.bmp"));
            var direct = new TesseractOcrRecognizer().Recognize(image, Options);
            var graph = new NodeGraph([
                new("source", "ImageInput", "source", JsonDefaults.Element(new ImageInputParameters { Path = "digits.bmp", PixelFormat = PixelFormat.Mono8 })),
                new("ocr", "OCR", "ocr", JsonDefaults.Element(Options)),
                new("display", "TextDisplay", "display", JsonDefaults.Element(new TextDisplayParameters { Prefix = "OCR: " }))
            ], [new("source", "Image", "ocr", "Image"), new("ocr", "Text", "display", "String")]);
            var registry = new NodeRegistry();
            registry.Register("ImageInput", () => new ImageInputNode()); registry.Register("OCR", () => new OcrNode());
            registry.Register("TextDisplay", () => new TextDisplayNode());
            using var run = await new RuntimeExecutor(registry).ExecuteAsync(graph, directory);
            Assert.True(run.Success, run.ErrorMessage); Assert.True(direct.Success, direct.FailureReason);
            var actual = run.Nodes["ocr"].Output<OcrResult>("Result");
            Assert.Equal(direct.Text, actual.Text); Assert.Equal(direct.Confidence, actual.Confidence);
            Assert.Equal(direct.Words.ToArray(), actual.Words.ToArray());
            Assert.Equal("OCR: 123456", run.Nodes["display"].Output<string>("Text"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ImageFrame TextImage(string text, ImageCoordinateSpace? space = null, bool paddedStride = false,
        byte background = 255, byte foreground = 0, int noise = 0)
    {
        using var mat = new Mat(150, 600, MatType.CV_8UC1, Scalar.All(background));
        if (text.Length > 0) Cv2.PutText(mat, text, new Point(30, 100), HersheyFonts.HersheySimplex, 2.3, Scalar.All(foreground), 4, LineTypes.AntiAlias);
        if (noise > 0)
        {
            var random = new Random(20261111);
            var rows = mat.Rows; var cols = mat.Cols;
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < cols; x++) mat.Set(y, x, (byte)Math.Clamp(mat.At<byte>(y, x) + random.Next(-noise, noise + 1), 0, 255));
        }
        using var copy = FrameFactory.CopyFromMat(mat, PixelFormat.Mono8);
        using var source = copy.Lease(); using var builder = new ImageFrameBuilder(600, 150, PixelFormat.Mono8, paddedStride ? 617 : 600, space);
        for (var y = 0; y < 150; y++) source.GetRowSpan(y).CopyTo(builder.GetRowSpan(y));
        return builder.Build();
    }
}

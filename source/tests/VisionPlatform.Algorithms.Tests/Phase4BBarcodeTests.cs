using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;
using ZXing;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase4BBarcodeTests
{
    [Theory]
    [InlineData(BarcodeFormatSelection.Code128, BarcodeFormat.CODE_128, "VISION-2026", "VISION-2026")]
    [InlineData(BarcodeFormatSelection.Code39, BarcodeFormat.CODE_39, "VISION123", "VISION123")]
    [InlineData(BarcodeFormatSelection.EAN13, BarcodeFormat.EAN_13, "5901234123457", "5901234123457")]
    [InlineData(BarcodeFormatSelection.EAN8, BarcodeFormat.EAN_8, "96385074", "96385074")]
    [InlineData(BarcodeFormatSelection.UPCA, BarcodeFormat.UPC_A, "036000291452", "036000291452")]
    [InlineData(BarcodeFormatSelection.UPCE, BarcodeFormat.UPC_E, "01234565", "01234565")]
    [InlineData(BarcodeFormatSelection.ITF, BarcodeFormat.ITF, "1234567890", "1234567890")]
    [InlineData(BarcodeFormatSelection.Codabar, BarcodeFormat.CODABAR, "A123456B", "123456")]
    public void GenuineEncodedOneDimensionalFixturesDecodeOffline(BarcodeFormatSelection selection, BarcodeFormat format, string payload, string expected)
    {
        using var frame = Fixture(format, payload);
        var result = new ZxingCodeReader().Read(frame, new BarcodeReadOptions { Format = selection });
        Assert.True(result.Success, result.FailureReason); Assert.Equal(expected, result.Text);
        Assert.Equal(format.ToString(), result.Format); Assert.Single(result.Codes); Assert.NotEmpty(result.Codes[0].Points);
        Assert.All(result.Overlays, overlay => Assert.Equal(RecognitionOverlayKind.Points, overlay.Kind));
    }

    [Fact]
    public void QrDecoderReadsRealUtf8PayloadAndBarcodeReaderDoesNotAcceptQr()
    {
        using var qr = Fixture(BarcodeFormat.QR_CODE, "VisionStudio:工业视觉/2026", 240, 240);
        var result = new ZxingCodeReader(true).Read(qr, new QrCodeReadOptions());
        Assert.True(result.Success, result.FailureReason); Assert.Equal("VisionStudio:工业视觉/2026", result.Text); Assert.Equal("QR_CODE", result.Format);
        Assert.InRange(result.Codes[0].Points.Count, 3, 4);
        Assert.False(new ZxingCodeReader().Read(qr, new BarcodeReadOptions()).Success);
        using var barcode = Fixture(BarcodeFormat.CODE_128, "ONLY-ONE-DIMENSION");
        Assert.False(new ZxingCodeReader(true).Read(barcode, new QrCodeReadOptions()).Success);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QuarterTurnDecodingMapsLocalizationPointsBackToOriginalPixels(int turns)
    {
        using var original = Fixture(BarcodeFormat.CODE_128, "ROTATION-123");
        using var rotated = RotateFixture(original, turns);
        var reader = new ZxingCodeReader();
        var baseline = reader.Read(original, new BarcodeReadOptions { AutoRotate = false, TryHarder = false });
        var result = reader.Read(rotated, new BarcodeReadOptions { TryHarder = false });
        Assert.True(result.Success, result.FailureReason); Assert.Equal(baseline.Text, result.Text);
        var actual = result.Codes[0].Points;
        foreach (var point in baseline.Codes[0].Points)
        {
            var expected = RotatePoint(point, turns, original.Width, original.Height);
            Assert.Contains(actual, p => Math.Abs(p.X - expected.X) <= 1.1 && Math.Abs(p.Y - expected.Y) <= 1.1);
        }
        Assert.All(actual, p => { Assert.InRange(p.X, 0, rotated.Width - 1); Assert.InRange(p.Y, 0, rotated.Height - 1); });
        if (turns % 2 != 0)
            Assert.False(reader.Read(rotated, new BarcodeReadOptions { AutoRotate = false, TryHarder = false }).Success);
    }

    [Fact]
    public void SearchRoiPaddedStrideAndNestedViewPreserveOriginalCoordinatesAndPixels()
    {
        using var symbol = Fixture(BarcodeFormat.CODE_128, "ROI-1234");
        using var builder = new ImageFrameBuilder(symbol.Width + 100, symbol.Height + 80, PixelFormat.Mono8,
            symbol.Width + 117, new("sensor", 1000, 2000));
        for (var y = 0; y < builder.Height; y++) builder.GetRowSpan(y).Fill(255);
        using (var source = symbol.Lease())
            for (var y = 0; y < symbol.Height; y++) source.GetRowSpan(y).CopyTo(builder.GetRowSpan(y + 23).Slice(31, symbol.Width));
        using var canvas = builder.Build(); var before = Pixels(canvas);
        var roi = new RotatedRectRoi(1031 + symbol.Width / 2.0, 2023 + symbol.Height / 2.0, symbol.Width, symbol.Height, 0, "sensor");
        var reader = new ZxingCodeReader();
        var baseline = reader.Read(symbol, new BarcodeReadOptions());
        var result = reader.Read(canvas, new BarcodeReadOptions(), roi);
        using var view = canvas.CreateView(11, 7, canvas.Width - 11, canvas.Height - 7);
        var nested = reader.Read(view, new BarcodeReadOptions(), roi);
        Assert.True(result.Success, result.FailureReason); Assert.Equal("sensor", result.CoordinateSpaceId);
        Assert.Equal(result.Codes[0].Points, nested.Codes[0].Points);
        for (var i = 0; i < baseline.Codes[0].Points.Count; i++)
        {
            Assert.Equal(baseline.Codes[0].Points[i].X + 1031, result.Codes[0].Points[i].X, 6);
            Assert.Equal(baseline.Codes[0].Points[i].Y + 2023, result.Codes[0].Points[i].Y, 6);
        }
        Assert.Equal(before, Pixels(canvas));
        var excluded = reader.Read(canvas, new BarcodeReadOptions(), new(1020, 2020, 20, 20, 0, "sensor"));
        Assert.False(excluded.Success); Assert.Empty(excluded.Codes); Assert.Empty(excluded.Overlays);
    }

    [Fact]
    public void ParameterRoiIsExplicitAndConnectedRoiHasPriority()
    {
        using var frame = Fixture(BarcodeFormat.CODE_128, "INPUT-ROI");
        var options = new BarcodeReadOptions { UseRoi = true, RoiX = 0, RoiY = 0, RoiWidth = 10, RoiHeight = 10 };
        Assert.False(new ZxingCodeReader().Read(frame, options).Success);
        Assert.True(new ZxingCodeReader().Read(frame, options, new(frame.Width / 2.0, frame.Height / 2.0, frame.Width, frame.Height)).Success);
    }

    [Fact]
    public void TryInvertedCanReadLightModulesOnDarkBackgroundWithoutChangingInput()
    {
        using var normal = Fixture(BarcodeFormat.QR_CODE, "INVERTED-QR", 240, 240);
        using var lease = normal.Lease();
        using var builder = new ImageFrameBuilder(normal.Width, normal.Height, PixelFormat.Mono8);
        for (var y = 0; y < normal.Height; y++) for (var x = 0; x < normal.Width; x++)
            builder.GetRowSpan(y)[x] = (byte)(255 - lease.GetRowSpan(y)[x]);
        using var inverted = builder.Build(); var before = Pixels(inverted);
        var result = new ZxingCodeReader(true).Read(inverted, new QrCodeReadOptions { TryInverted = true });
        Assert.True(result.Success, result.FailureReason); Assert.Equal("INVERTED-QR", result.Text); Assert.Equal(before, Pixels(inverted));
    }

    [Theory]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public void ReadersRequireExplicitMonoConversion(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(80, 80, format); using var frame = builder.Build();
        Assert.Contains("Mono8", Assert.Throws<ArgumentException>(() => new ZxingCodeReader().Read(frame, new BarcodeReadOptions())).Message);
    }

    [Fact]
    public void InvalidParametersRoiAndPixelBudgetGiveExplicitErrors()
    {
        using var frame = Fixture(BarcodeFormat.CODE_128, "VALIDATION");
        Assert.NotEmpty(ZxingCodeReader.Validate(new BarcodeReadOptions { Format = (BarcodeFormatSelection)99 }));
        Assert.NotEmpty(ZxingCodeReader.Validate(new QrCodeReadOptions { MaximumPixels = 0 }));
        Assert.NotEmpty(ZxingCodeReader.Validate(new QrCodeReadOptions { UseRoi = true, RoiWidth = double.NaN }));
        Assert.Throws<ArgumentException>(() => new ZxingCodeReader().Read(frame, new BarcodeReadOptions { MaximumPixels = 100 }));
        Assert.Throws<ArgumentException>(() => new ZxingCodeReader().Read(frame, new BarcodeReadOptions(), new(50, 50, 40, 40, 20)));
        Assert.Throws<ArgumentException>(() => new ZxingCodeReader().Read(frame, new BarcodeReadOptions(), new(-10, 50, 40, 40)));
    }

    [Fact]
    public void RepeatedCallsAreStableAndBlankCallNeverReusesSuccessfulDataOrLeaksInputReferences()
    {
        using var frame = Fixture(BarcodeFormat.CODE_128, "REPEAT-123"); var initialReferences = frame.BufferHandle.ReferenceCount;
        using var builder = new ImageFrameBuilder(frame.Width, frame.Height, PixelFormat.Mono8);
        for (var y = 0; y < builder.Height; y++) builder.GetRowSpan(y).Fill(255);
        using var blank = builder.Build(); var reader = new ZxingCodeReader();
        var first = reader.Read(frame, new BarcodeReadOptions());
        for (var repeat = 0; repeat < 12; repeat++)
        {
            var result = reader.Read(frame, new BarcodeReadOptions()); Assert.True(result.Success); Assert.Equal(first.Text, result.Text);
            Assert.Equal(first.Codes[0].Points, result.Codes[0].Points);
            var failure = reader.Read(blank, new BarcodeReadOptions()); Assert.False(failure.Success); Assert.Empty(failure.Codes);
            Assert.Empty(failure.Text); Assert.Empty(failure.Format); Assert.Empty(failure.Overlays); Assert.NotEmpty(failure.FailureReason!);
        }
        Assert.Equal(initialReferences, frame.BufferHandle.ReferenceCount); Assert.False(frame.IsDisposed);
    }

    [Fact]
    public void CancellationIsObservedAndOwnedRegionReferencesAreReleased()
    {
        using var frame = Fixture(BarcodeFormat.QR_CODE, "CANCELLATION", 200, 200);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var references = frame.BufferHandle.ReferenceCount;
        Assert.Throws<OperationCanceledException>(() => new ZxingCodeReader(true).Read(frame, new QrCodeReadOptions(), cancellationToken: cancellation.Token));
        Assert.Equal(references, frame.BufferHandle.ReferenceCount);
    }

    [Fact]
    public async Task BothRealNodesUseRuntimeAndCurrentOutputsOnly()
    {
        var registry = new NodeRegistry(); registry.Register(() => new BarcodeReaderNode()); registry.Register(() => new QRCodeReaderNode());
        var executor = new RuntimeExecutor(registry);
        foreach (var qr in new[] { false, true })
        {
            using var image = Fixture(qr ? BarcodeFormat.QR_CODE : BarcodeFormat.CODE_128, "RUNTIME-123", qr ? 240 : 420, qr ? 240 : 120);
            var spec = new NodeSpecification("reader", qr ? "QRCodeReader" : "BarcodeReader", "Decode",
                qr ? JsonDefaults.Element(new QrCodeReadOptions()) : JsonDefaults.Element(new BarcodeReadOptions()));
            using var run = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image }, ".");
            Assert.True(run.Success); var node = run.Nodes["reader"];
            Assert.True(node.Output<bool>("Success")); Assert.Equal("RUNTIME-123", node.Output<string>("Text")); Assert.Equal(1, node.Output<int>("Count"));
            Assert.True(node.Output<CodeReadResult>("Result").Success);
            using var builder = new ImageFrameBuilder(40, 40, PixelFormat.Mono8); using var blank = builder.Build();
            using var next = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = blank }, ".");
            Assert.True(next.Success); Assert.False(next.Nodes["reader"].Output<bool>("Success")); Assert.Equal(0, next.Nodes["reader"].Output<int>("Count"));
            Assert.Empty(next.Nodes["reader"].Output<string>("Text"));
        }
    }

    // Genuine encoded fixtures; no external service, camera, proprietary binaries, or hardcoded decoder results.
    private static ImageFrame Fixture(BarcodeFormat format, string payload, int width = 420, int height = 120)
    {
        var bits = new MultiFormatWriter().encode(payload, format, width, height,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 12 });
        using var builder = new ImageFrameBuilder(bits.Width, bits.Height, PixelFormat.Mono8);
        for (var y = 0; y < bits.Height; y++) for (var x = 0; x < bits.Width; x++) builder.GetRowSpan(y)[x] = bits[x, y] ? (byte)0 : (byte)255;
        return builder.Build();
    }

    private static ImageFrame RotateFixture(ImageFrame image, int turns)
    {
        using var builder = new ImageFrameBuilder(turns % 2 == 0 ? image.Width : image.Height,
            turns % 2 == 0 ? image.Height : image.Width, PixelFormat.Mono8);
        using var lease = image.Lease();
        for (var y = 0; y < image.Height; y++) for (var x = 0; x < image.Width; x++)
        {
            var target = RotatePoint(new(x, y), turns, image.Width, image.Height);
            builder.GetRowSpan((int)target.Y)[(int)target.X] = lease.GetRowSpan(y)[x];
        }
        return builder.Build();
    }
    private static Point2D RotatePoint(Point2D p, int turns, int width, int height) => turns switch
    {
        1 => new(height - 1 - p.Y, p.X), 2 => new(width - 1 - p.X, height - 1 - p.Y),
        3 => new(p.Y, width - 1 - p.X), _ => p
    };
    private static byte[] Pixels(ImageFrame image)
    {
        using var lease = image.Lease(); var result = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++) lease.GetRowSpan(y).CopyTo(result.AsSpan(y * image.Width, image.Width));
        return result;
    }
}

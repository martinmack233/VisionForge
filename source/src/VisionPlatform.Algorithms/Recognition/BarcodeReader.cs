using System.Diagnostics;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Recognition;
using ZXing;
using ZXing.Common;

namespace VisionPlatform.Algorithms.Recognition;

public enum BarcodeFormatSelection { Common1D, Code128, Code39, EAN13, EAN8, UPCA, UPCE, ITF, Codabar }

public record CodeReadOptions
{
    public bool TryHarder { get; init; } = true;
    public bool TryInverted { get; init; }
    /// <summary>Explicit adapter rotations, with localization points mapped back to the input.</summary>
    public bool AutoRotate { get; init; } = true;
    public int MaximumPixels { get; init; } = 16_000_000;
    public bool UseRoi { get; init; }
    public double RoiX { get; init; }
    public double RoiY { get; init; }
    public double RoiWidth { get; init; } = 640;
    public double RoiHeight { get; init; } = 480;
}

public sealed record BarcodeReadOptions : CodeReadOptions
{
    public BarcodeFormatSelection Format { get; init; } = BarcodeFormatSelection.Common1D;
}

public sealed record QrCodeReadOptions : CodeReadOptions;

/// <summary>Headless code decoder. Borrowed images remain immutable and owned by the caller.</summary>
public interface ICodeReader
{
    CodeReadResult Read(ImageFrame image, CodeReadOptions options, RotatedRectRoi? roi = null,
        CancellationToken cancellationToken = default);
}

/// <summary>ZXing.Net 0.16.11, one current symbol per call; no previous-frame result cache.</summary>
public sealed class ZxingCodeReader(bool qrCodeOnly = false) : ICodeReader
{
    public static IReadOnlyList<string> Validate(CodeReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.MaximumPixels is < 1 or > 100_000_000)
            errors.Add("MaximumPixels must be between 1 and 100000000; use a smaller ROI for large images.");
        if (options is BarcodeReadOptions barcode && !Enum.IsDefined(barcode.Format))
            errors.Add("Format must be a valid supported one-dimensional barcode format.");
        if (options.UseRoi && (!double.IsFinite(options.RoiX) || !double.IsFinite(options.RoiY) ||
            !double.IsFinite(options.RoiWidth) || !double.IsFinite(options.RoiHeight) ||
            options.RoiWidth <= 0 || options.RoiHeight <= 0))
            errors.Add("ROI coordinates must be finite and ROI width and height must be positive.");
        return errors;
    }

    public CodeReadResult Read(ImageFrame image, CodeReadOptions options, RotatedRectRoi? roi = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        if (image.PixelFormat != PixelFormat.Mono8)
            throw new ArgumentException("Code readers require Mono8. Use PixelFormatConvert explicitly for RGB/BGR or Bayer images.", nameof(image));
        var timer = Stopwatch.StartNew();
        if (roi is null && options.UseRoi)
            roi = new(options.RoiX + options.RoiWidth / 2, options.RoiY + options.RoiHeight / 2,
                options.RoiWidth, options.RoiHeight, 0, image.CoordinateSpace.Id);
        using var region = RecognitionRegion.Open(image, roi, options.MaximumPixels);
        var pixels = new byte[checked(region.Width * region.Height)];
        using (var lease = region.Lease())
        {
            for (var y = 0; y < region.Height; y++)
            {
                if ((y & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                lease.GetRowSpan(y).CopyTo(pixels.AsSpan(y * region.Width, region.Width));
            }
        }
        var formats = qrCodeOnly ? new[] { BarcodeFormat.QR_CODE } : GetFormats((options as BarcodeReadOptions)?.Format ?? BarcodeFormatSelection.Common1D);
        var width = region.Width;
        var height = region.Height;
        for (var turn = 0; turn < (options.AutoRotate ? 4 : 1); turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A fresh reader prevents decoder state or results from leaking across calls/rotations.
            // Generic AutoRotate does not undo its outer rotation on ResultPoints in the pinned version.
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = false,
                Options = new DecodingOptions { TryHarder = options.TryHarder, TryInverted = options.TryInverted, PossibleFormats = formats }
            };
            var decoded = reader.Decode(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded is not null)
            {
                var points = Array.AsReadOnly((decoded.ResultPoints ?? []).Select(p =>
                {
                    var original = ToOriginal(p.X, p.Y, turn, region.Width, region.Height);
                    return new Point2D(original.X + region.CoordinateSpace.OriginX, original.Y + region.CoordinateSpace.OriginY);
                }).ToArray());
                var text = decoded.Text ?? "";
                var code = new CodeReading(text, decoded.BarcodeFormat.ToString(), points);
                timer.Stop();
                IReadOnlyList<RecognitionOverlay> overlays = points.Count == 0 ? [] :
                    Array.AsReadOnly(new[] { new RecognitionOverlay(RecognitionOverlayKind.Points, text, points, image.CoordinateSpace.Id) });
                return new(true, Array.AsReadOnly(new[] { code }), image.CoordinateSpace.Id, timer.Elapsed, overlays);
            }
            if (turn < (options.AutoRotate ? 3 : 0))
            {
                pixels = RotateClockwise(pixels, width, height, cancellationToken);
                (width, height) = (height, width);
            }
        }
        timer.Stop();
        return new(false, [], image.CoordinateSpace.Id, timer.Elapsed, [],
            qrCodeOnly ? "No QR code was decoded in the current image/ROI." : "No supported one-dimensional barcode was decoded in the current image/ROI.");
    }

    private static BarcodeFormat[] GetFormats(BarcodeFormatSelection format) => format switch
    {
        BarcodeFormatSelection.Common1D => [BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
            BarcodeFormat.UPC_A, BarcodeFormat.UPC_E, BarcodeFormat.ITF, BarcodeFormat.CODABAR],
        BarcodeFormatSelection.Code128 => [BarcodeFormat.CODE_128],
        BarcodeFormatSelection.Code39 => [BarcodeFormat.CODE_39],
        BarcodeFormatSelection.EAN13 => [BarcodeFormat.EAN_13],
        BarcodeFormatSelection.EAN8 => [BarcodeFormat.EAN_8],
        BarcodeFormatSelection.UPCA => [BarcodeFormat.UPC_A],
        BarcodeFormatSelection.UPCE => [BarcodeFormat.UPC_E],
        BarcodeFormatSelection.ITF => [BarcodeFormat.ITF],
        BarcodeFormatSelection.Codabar => [BarcodeFormat.CODABAR],
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static Point2D ToOriginal(double x, double y, int turn, int originalWidth, int originalHeight) => turn switch
    {
        0 => new(x, y),
        1 => new(y, originalHeight - 1 - x),
        2 => new(originalWidth - 1 - x, originalHeight - 1 - y),
        3 => new(originalWidth - 1 - y, x),
        _ => throw new ArgumentOutOfRangeException(nameof(turn))
    };

    private static byte[] RotateClockwise(byte[] input, int width, int height, CancellationToken token)
    {
        var result = new byte[input.Length];
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) token.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++) result[x * height + height - 1 - y] = input[y * width + x];
        }
        return result;
    }
}

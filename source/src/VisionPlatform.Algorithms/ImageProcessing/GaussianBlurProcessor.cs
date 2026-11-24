using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;

public enum GaussianBorderType { Constant, Replicate, Reflect, Reflect101 }

public sealed record GaussianBlurOptions
{
    public int KernelSize { get; init; } = 5;
    public double SigmaX { get; init; } = 1.2;
    public double SigmaY { get; init; }
    public GaussianBorderType BorderType { get; init; } = GaussianBorderType.Reflect101;
}

public static class GaussianBlurProcessor
{
    public static IReadOnlyList<string> Validate(GaussianBlurOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.KernelSize <= 0 || options.KernelSize % 2 == 0) errors.Add("KernelSize must be a positive odd number.");
        if (!double.IsFinite(options.SigmaX) || options.SigmaX < 0) errors.Add("SigmaX must be finite and nonnegative; zero derives sigma from the kernel.");
        if (!double.IsFinite(options.SigmaY) || options.SigmaY < 0) errors.Add("SigmaY must be finite and nonnegative; zero uses SigmaX.");
        if (!Enum.IsDefined(options.BorderType)) errors.Add("BorderType is not supported.");
        return errors;
    }

    /// <summary>ROI is in original pixel coordinates. Output is an independently filtered ROI image with its original origin.
    /// Its border is isolated: neighboring pixels outside ROI do not contribute. Input pixels are never modified.</summary>
    public static ImageFrame Process(ImageFrame image, GaussianBlurOptions options, RectRoi? roi = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        if (image.PixelFormat.IsBayer()) throw new NotSupportedException("Bayer input requires an explicit RGB8/BGR8 conversion before Gaussian filtering.");
        ct.ThrowIfCancellationRequested();
        using var view = CreateInputView(image, roi);
        using var input = new OpenCvImageLease(view);
        using var builder = new ImageFrameBuilder(view.Width, view.Height, image.PixelFormat, coordinateSpace: view.CoordinateSpace);
        using var output = Mat.FromPixelData(view.Height, view.Width, image.PixelFormat.BytesPerPixel() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3,
            builder.DataPointer, builder.Stride);
        var border = options.BorderType switch
        {
            GaussianBorderType.Constant => BorderTypes.Constant,
            GaussianBorderType.Replicate => BorderTypes.Replicate,
            GaussianBorderType.Reflect => BorderTypes.Reflect,
            GaussianBorderType.Reflect101 => BorderTypes.Reflect101,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        Cv2.GaussianBlur(input.Mat, output, new Size(options.KernelSize, options.KernelSize),
            options.SigmaX, options.SigmaY, border | BorderTypes.Isolated);
        ct.ThrowIfCancellationRequested();
        return builder.Build(image.FrameId, image.Timestamp);
    }

    private static ImageFrame CreateInputView(ImageFrame image, RectRoi? roi)
    {
        if (roi is null) return image.Share();
        var localX = roi.X - image.CoordinateSpace.OriginX;
        var localY = roi.Y - image.CoordinateSpace.OriginY;
        if (new[] { localX, localY, roi.Width, roi.Height }.Any(v => !double.IsFinite(v) || Math.Abs(v - Math.Round(v)) > 1e-9 || v < 0 || v > int.MaxValue))
            throw new ArgumentException("Gaussian ROI must have integer pixel bounds relative to its input image.", nameof(roi));
        return image.CreateView(checked((int)localX), checked((int)localY), checked((int)roi.Width), checked((int)roi.Height));
    }
}

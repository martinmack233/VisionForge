using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;

public sealed record MedianBlurOptions
{
    [DisplayName("中值核大小（正奇数）"), Range(1, 255)]
    public int KernelSize { get; init; } = 3;
}

public enum ThresholdMode { Binary, BinaryInv, Trunc, ToZero, ToZeroInv }
public sealed record ThresholdOptions
{
    [DisplayName("固定阈值"), Range(0, 255)]
    public double ThresholdValue { get; init; } = 127;
    [DisplayName("最大输出值"), Range(0, 255)]
    public double MaxValue { get; init; } = 255;
    [DisplayName("阈值类型")]
    public ThresholdMode ThresholdType { get; init; } = ThresholdMode.Binary;
}

public enum MorphologyOperation { Erode, Dilate, Open, Close, Gradient }
public enum MorphologyKernelShape { Rectangle, Ellipse, Cross }
public sealed record MorphologyOptions
{
    [DisplayName("形态学操作")]
    public MorphologyOperation Operation { get; init; } = MorphologyOperation.Open;
    [DisplayName("结构元素宽度"), Range(1, 255)]
    public int KernelWidth { get; init; } = 3;
    [DisplayName("结构元素高度"), Range(1, 255)]
    public int KernelHeight { get; init; } = 3;
    [DisplayName("结构元素形状")]
    public MorphologyKernelShape KernelShape { get; init; } = MorphologyKernelShape.Rectangle;
    [DisplayName("迭代次数"), Range(1, 100)]
    public int Iterations { get; init; } = 1;
}

/// <summary>All processors borrow input, preserve format, and allocate an independent output.
/// Optional ROI uses original coordinates and an isolated border. Bayer requires explicit conversion.</summary>
internal static class ProcessingFrame
{
    public static ImageFrame View(ImageFrame image, RectRoi? roi, bool monoOnly = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelFormat.IsBayer())
            throw new NotSupportedException("Bayer input requires explicit RGB8/BGR8 conversion before neighborhood processing.");
        if (monoOnly && image.PixelFormat != PixelFormat.Mono8)
            throw new NotSupportedException("Threshold requires Mono8. Add PixelFormatConvert to Mono8 explicitly; color input is not converted silently.");
        if (roi is null) return image.Share();
        var x = roi.X - image.CoordinateSpace.OriginX; var y = roi.Y - image.CoordinateSpace.OriginY;
        if (new[] { x, y, roi.Width, roi.Height }.Any(v => !double.IsFinite(v) || v < 0 || v > int.MaxValue || Math.Abs(v - Math.Round(v)) > 1e-9))
            throw new ArgumentException("Processing ROI must have integer pixel bounds relative to its input image.", nameof(roi));
        return image.CreateView((int)x, (int)y, (int)roi.Width, (int)roi.Height);
    }

    public static ImageFrame Execute(ImageFrame view, Action<Mat, Mat> process, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var input = new OpenCvImageLease(view);
        using var builder = new ImageFrameBuilder(view.Width, view.Height, view.PixelFormat, coordinateSpace: view.CoordinateSpace);
        using var output = Mat.FromPixelData(view.Height, view.Width,
            view.PixelFormat.BytesPerPixel() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3, builder.DataPointer, builder.Stride);
        process(input.Mat, output);
        ct.ThrowIfCancellationRequested();
        return builder.Build(view.FrameId, view.Timestamp);
    }

    public static void ThrowInvalid(IReadOnlyList<string> errors)
    { if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors)); }
}

public sealed class MedianBlurProcessor : IImageProcessingAlgorithm<MedianBlurOptions>
{
    public string AlgorithmId => "opencv.median";
    public IReadOnlyList<string> Validate(MedianBlurOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.KernelSize is < 1 or > 255 || options.KernelSize % 2 == 0
            ? ["KernelSize must be an odd integer in [1,255]."] : [];
    }
    public ImageFrame Process(ImageFrame image, MedianBlurOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var view = ProcessingFrame.View(image, roi);
        // k=1 is an explicitly defined independent identity output.
        return ProcessingFrame.Execute(view, (input, output) => Apply(input, output, options.KernelSize), cancellationToken);
    }

    internal static void Apply(Mat input, Mat output, int kernelSize)
    {
        if (kernelSize == 1) input.CopyTo(output);
        else Cv2.MedianBlur(input, output, kernelSize);
    }
}

public sealed class ThresholdProcessor : IImageProcessingAlgorithm<ThresholdOptions>
{
    public string AlgorithmId => "opencv.threshold";
    public IReadOnlyList<string> Validate(ThresholdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!double.IsFinite(options.ThresholdValue) || options.ThresholdValue is < 0 or > 255) errors.Add("ThresholdValue must be finite in [0,255].");
        if (!double.IsFinite(options.MaxValue) || options.MaxValue is < 0 or > 255) errors.Add("MaxValue must be finite in [0,255].");
        if (!Enum.IsDefined(options.ThresholdType)) errors.Add("ThresholdType is invalid.");
        return errors;
    }
    public ImageFrame Process(ImageFrame image, ThresholdOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var view = ProcessingFrame.View(image, roi, monoOnly: true);
        var mode = options.ThresholdType switch
        {
            ThresholdMode.Binary => ThresholdTypes.Binary, ThresholdMode.BinaryInv => ThresholdTypes.BinaryInv,
            ThresholdMode.Trunc => ThresholdTypes.Trunc, ThresholdMode.ToZero => ThresholdTypes.Tozero,
            ThresholdMode.ToZeroInv => ThresholdTypes.TozeroInv, _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        return ProcessingFrame.Execute(view, (input, output) => Cv2.Threshold(input, output, options.ThresholdValue, options.MaxValue, mode), cancellationToken);
    }

    /// <summary>Number of nonzero output pixels, including Trunc/ToZero modes; not a binary-object count.</summary>
    public static int CountForeground(ImageFrame image, CancellationToken ct = default)
    {
        if (image.PixelFormat != PixelFormat.Mono8) throw new ArgumentException("Foreground counting requires Mono8.", nameof(image));
        var total = 0; using var lease = image.Lease();
        for (var y = 0; y < image.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var value in lease.GetRowSpan(y)[..image.Width]) if (value != 0) total = checked(total + 1);
        }
        return total;
    }
}

public sealed class MorphologyProcessor : IImageProcessingAlgorithm<MorphologyOptions>
{
    public string AlgorithmId => "opencv.morphology";
    public IReadOnlyList<string> Validate(MorphologyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.KernelWidth is < 1 or > 255 || options.KernelHeight is < 1 or > 255) errors.Add("KernelWidth and KernelHeight must be in [1,255].");
        if (options.Iterations is < 1 or > 100) errors.Add("Iterations must be in [1,100].");
        if (!Enum.IsDefined(options.Operation)) errors.Add("Operation is invalid.");
        if (!Enum.IsDefined(options.KernelShape)) errors.Add("KernelShape is invalid.");
        return errors;
    }
    public ImageFrame Process(ImageFrame image, MorphologyOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var view = ProcessingFrame.View(image, roi);
        var shape = options.KernelShape switch
        { MorphologyKernelShape.Rectangle => MorphShapes.Rect, MorphologyKernelShape.Ellipse => MorphShapes.Ellipse, MorphologyKernelShape.Cross => MorphShapes.Cross, _ => throw new ArgumentOutOfRangeException(nameof(options)) };
        using var kernel = Cv2.GetStructuringElement(shape, new Size(options.KernelWidth, options.KernelHeight));
        var operation = options.Operation switch
        {
            MorphologyOperation.Erode => MorphTypes.Erode, MorphologyOperation.Dilate => MorphTypes.Dilate,
            MorphologyOperation.Open => MorphTypes.Open, MorphologyOperation.Close => MorphTypes.Close,
            MorphologyOperation.Gradient => MorphTypes.Gradient, _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        // OpenCV's morphology default constant border is neutral for erosion/dilation; isolated ROI.
        return ProcessingFrame.Execute(view, (input, output) => Cv2.MorphologyEx(input, output, operation, kernel,
            iterations: options.Iterations, borderType: BorderTypes.Constant | BorderTypes.Isolated), cancellationToken);
    }
}

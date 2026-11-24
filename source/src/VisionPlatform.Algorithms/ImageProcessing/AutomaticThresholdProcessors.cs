using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.ImageProcessing;

public enum BinaryThresholdType { Binary, BinaryInv }
public enum AdaptiveThresholdMethod { Mean, Gaussian }

public sealed record OtsuThresholdOptions
{
    [DisplayName("最大输出值"), Range(0, 255)] public double MaxValue { get; init; } = 255;
    [DisplayName("二值输出类型")] public BinaryThresholdType ThresholdType { get; init; }
}

public sealed record AdaptiveThresholdOptions
{
    [DisplayName("自适应阈值方法")] public AdaptiveThresholdMethod Method { get; init; } = AdaptiveThresholdMethod.Gaussian;
    [DisplayName("邻域大小（至少3的奇数）"), Range(3, 255)] public int BlockSize { get; init; } = 11;
    [DisplayName("局部均值减去 C"), Range(-255, 255)] public double C { get; init; } = 2;
    [DisplayName("最大输出值"), Range(0, 255)] public double MaxValue { get; init; } = 255;
    [DisplayName("二值输出类型")] public BinaryThresholdType ThresholdType { get; init; }
}

/// <summary>The caller owns BinaryImage. CalculatedThreshold is the measured OpenCV Otsu threshold.</summary>
public sealed record OtsuThresholdResult(ImageFrame BinaryImage, double CalculatedThreshold) : IDisposable
{
    public void Dispose() => BinaryImage.Dispose();
}

internal static class EnhancementValidation
{
    public static ImageFrame MonoView(ImageFrame image, RectRoi? roi, string algorithm)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelFormat != PixelFormat.Mono8)
            throw new NotSupportedException(algorithm + " requires Mono8. Use an explicit PixelFormatConvert node; Bayer and color inputs are not silently reinterpreted.");
        return ProcessingFrame.View(image, roi);
    }
    public static void BinaryOptions(double maximum, BinaryThresholdType type, List<string> errors)
    {
        if (!double.IsFinite(maximum) || maximum is < 0 or > 255) errors.Add("MaxValue must be finite in [0,255].");
        if (!Enum.IsDefined(type)) errors.Add("ThresholdType must be Binary or BinaryInv.");
    }
    public static ThresholdTypes ThresholdType(BinaryThresholdType type) => type switch
    {
        BinaryThresholdType.Binary => ThresholdTypes.Binary,
        BinaryThresholdType.BinaryInv => ThresholdTypes.BinaryInv,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}

public sealed class OtsuThresholdProcessor : IImageProcessingAlgorithm<OtsuThresholdOptions>
{
    public string AlgorithmId => "opencv.otsu";
    public IReadOnlyList<string> Validate(OtsuThresholdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); var errors = new List<string>();
        EnhancementValidation.BinaryOptions(options.MaxValue, options.ThresholdType, errors); return errors;
    }
    public OtsuThresholdResult ProcessWithThreshold(ImageFrame image, OtsuThresholdOptions options, RectRoi? roi = null,
        CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options)); cancellationToken.ThrowIfCancellationRequested();
        using var view = EnhancementValidation.MonoView(image, roi, "OtsuThreshold");
        var threshold = 0.0;
        var result = ProcessingFrame.Execute(view, (input, output) => threshold = Cv2.Threshold(input, output, 0, options.MaxValue,
            EnhancementValidation.ThresholdType(options.ThresholdType) | ThresholdTypes.Otsu), cancellationToken);
        return new(result, threshold);
    }
    public ImageFrame Process(ImageFrame image, OtsuThresholdOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
        => ProcessWithThreshold(image, options, roi, cancellationToken).BinaryImage;
}

public sealed class AdaptiveThresholdProcessor : IImageProcessingAlgorithm<AdaptiveThresholdOptions>
{
    public string AlgorithmId => "opencv.adaptive-threshold";
    public IReadOnlyList<string> Validate(AdaptiveThresholdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); var errors = new List<string>();
        EnhancementValidation.BinaryOptions(options.MaxValue, options.ThresholdType, errors);
        if (!Enum.IsDefined(options.Method)) errors.Add("Adaptive Method must be Mean or Gaussian.");
        if (options.BlockSize is < 3 or > 255 || options.BlockSize % 2 == 0) errors.Add("BlockSize must be an odd integer in [3,255].");
        if (!double.IsFinite(options.C) || options.C is < -255 or > 255) errors.Add("C must be finite in [-255,255].");
        return errors;
    }
    public ImageFrame Process(ImageFrame image, AdaptiveThresholdOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options)); cancellationToken.ThrowIfCancellationRequested();
        using var view = EnhancementValidation.MonoView(image, roi, "AdaptiveThreshold");
        var method = options.Method == AdaptiveThresholdMethod.Mean ? AdaptiveThresholdTypes.MeanC : AdaptiveThresholdTypes.GaussianC;
        return ProcessingFrame.Execute(view, (input, output) => Cv2.AdaptiveThreshold(input, output, options.MaxValue, method,
            EnhancementValidation.ThresholdType(options.ThresholdType), options.BlockSize, options.C), cancellationToken);
    }
}

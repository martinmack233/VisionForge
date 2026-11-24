using VisionPlatform.Contracts.Results;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.ImageProcessing;

public sealed record NormalizeOptions
{
    [DisplayName("目标最小值"), Range(0, 255)]
    [Description("按实际输入的共同最小/最大值归一化，输出仍为8位；常量输入映射到Minimum。RGB/BGR共同统计而非逐通道分别均衡。")]
    public double Minimum { get; init; }
    [DisplayName("目标最大值"), Range(0, 255)] public double Maximum { get; init; } = 255;
}
public sealed record EqualizeHistOptions
{
    [DisplayName("直方图区域（可选）")]
    [Description("区域边界使用输入原图整数像素坐标。仅处理并输出该区域，保留原点，不将区域外像素纳入直方图。")]
    public RectRoi? ROI { get; init; }
}
public sealed record ClaheOptions
{
    [DisplayName("对比度限制"), Range(0.000001, 1000)]
    [Description("Mono8局部直方图限幅阈值，正数；不对彩色各通道独立均衡。ROI独立统计/插值，结果可不同于整图处理后裁切。")]
    public double ClipLimit { get; init; } = 2;
    [DisplayName("每轴分块数"), Range(1, 64)]
    [Description("TileGridSize=8 表示8×8分块数量，不是每块8个像素。")]
    public int TileGridSize { get; init; } = 8;
}

/// <summary>One common min/max mapping across all input channels. Channel order and CV_8U depth stay unchanged.</summary>
public sealed class NormalizeProcessor : IImageProcessingAlgorithm<NormalizeOptions>
{
    public string AlgorithmId => "opencv.normalize-minmax";
    public IReadOnlyList<string> Validate(NormalizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !double.IsFinite(options.Minimum) || !double.IsFinite(options.Maximum) || options.Minimum < 0 || options.Maximum > 255 || options.Minimum >= options.Maximum
            ? ["Normalize requires finite 0 <= Minimum < Maximum <= 255."] : [];
    }
    public ImageFrame Process(ImageFrame image, NormalizeOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options)); cancellationToken.ThrowIfCancellationRequested();
        using var view = ProcessingFrame.View(image, roi);
        return ProcessingFrame.Execute(view, (input, output) => Cv2.Normalize(input, output, options.Minimum, options.Maximum, NormTypes.MinMax), cancellationToken);
    }
}

public sealed class EqualizeHistProcessor : IImageProcessingAlgorithm<EqualizeHistOptions>
{
    public string AlgorithmId => "opencv.equalize-hist";
    public IReadOnlyList<string> Validate(EqualizeHistOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ROI is not { } roi) return [];
        return new[] { roi.X, roi.Y, roi.Width, roi.Height }.Any(v => !double.IsFinite(v) || Math.Abs(v - Math.Round(v)) > 1e-9) || roi.Width <= 0 || roi.Height <= 0
            ? ["EqualizeHist ROI requires finite integer boundaries with positive width/height."] : [];
    }
    public ImageFrame Process(ImageFrame image, EqualizeHistOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options)); cancellationToken.ThrowIfCancellationRequested();
        using var view = EnhancementValidation.MonoView(image, roi ?? options.ROI, "EqualizeHist");
        return ProcessingFrame.Execute(view, (input, output) => Cv2.EqualizeHist(input, output), cancellationToken);
    }
}

public sealed class ClaheProcessor : IImageProcessingAlgorithm<ClaheOptions>
{
    public string AlgorithmId => "opencv.clahe";
    public IReadOnlyList<string> Validate(ClaheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); var errors = new List<string>();
        if (!double.IsFinite(options.ClipLimit) || options.ClipLimit is < 0.000001 or > 1000) errors.Add("ClipLimit must be finite in [0.000001,1000].");
        if (options.TileGridSize is < 1 or > 64) errors.Add("TileGridSize must be an integer number of tiles per axis in [1,64].");
        return errors;
    }
    public ImageFrame Process(ImageFrame image, ClaheOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options)); cancellationToken.ThrowIfCancellationRequested();
        using var view = EnhancementValidation.MonoView(image, roi, "CLAHE");
        using var algorithm = Cv2.CreateCLAHE(options.ClipLimit, new Size(options.TileGridSize, options.TileGridSize));
        return ProcessingFrame.Execute(view, (input, output) => algorithm.Apply(input, output), cancellationToken);
    }
}

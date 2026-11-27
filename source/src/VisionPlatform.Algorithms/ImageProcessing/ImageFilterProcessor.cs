using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;

public enum ImageFilterMethod
{
    [Display(Name = "均值（归一化方框）", Description = "对邻域等权平均，适用于轻度随机噪声，边缘也会变软。")] Mean,
    [Display(Name = "方框邻域和（Mono8）", Description = "输出 Float32 邻域和，不截断为 8 位；适用于局部总量统计。")] BoxSum,
    [Display(Name = "高斯滤波", Description = "对邻域按高斯加权，适用于近似高斯噪声。")] Gaussian,
    [Display(Name = "中值滤波", Description = "适用于椒盐和孤立脉冲噪声；边界固定使用复制。")] Median,
    [Display(Name = "双边滤波", Description = "按距离和颜色差加权，用于保边降噪；高分辨率优先选择局部区域。")] Bilateral,
    [Display(Name = "方向平滑", Description = "只沿一个方向取样。用于沿该方向平均波动；垂直于条纹取样可减弱条纹，同时也会平滑同方向细节。")] Directional
}

public enum FilterSamplingDirection
{
    [Display(Name = "沿横向取样（X 轴）")] Horizontal,
    [Display(Name = "沿纵向取样（Y 轴）")] Vertical
}

public sealed record ImageFilterOptions
{
    [DisplayName("滤波方式"), Description("保持输入像素格式，方框邻域和使用独立数值端口。Bayer 先显式转换。")]
    public ImageFilterMethod Method { get; init; }

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Mean), nameof(ImageFilterMethod.BoxSum))]
    [DisplayName("横向核宽（像素）"), Description("1–255 的正奇数；越大越平滑。"), Range(1, 255)]
    public int KernelWidth { get; init; } = 5;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Mean), nameof(ImageFilterMethod.BoxSum))]
    [DisplayName("纵向核高（像素）"), Description("1–255 的正奇数；两轴分别配置。"), Range(1, 255)]
    public int KernelHeight { get; init; } = 3;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Gaussian), nameof(ImageFilterMethod.Median), nameof(ImageFilterMethod.Directional))]
    [DisplayName("核大小／取样长度（像素）"), Description("1–255 的正奇数；方向平滑仅沿所选轴取样。"), Range(1, 255)]
    public int KernelSize { get; init; } = 5;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Gaussian))]
    [DisplayName("Sigma X（像素）"), Description("横向标准差；0 表示根据核大小推导。"), Range(0d, double.MaxValue)]
    public double SigmaX { get; init; } = 1.2;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Gaussian))]
    [DisplayName("Sigma Y（像素）"), Description("纵向标准差；0 表示采用横向标准差规则。"), Range(0d, double.MaxValue)]
    public double SigmaY { get; init; }

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Bilateral))]
    [DisplayName("邻域直径（像素）"), Description("3–31 的正奇数；越大耗时越长。"), Range(3, 31)]
    public int Diameter { get; init; } = 5;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Bilateral))]
    [DisplayName("颜色 Sigma（8 位灰度级）"), Description("越大越容易混合颜色差异明显的像素。"), Range(0.01, 1000)]
    public double SigmaColor { get; init; } = 25;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Bilateral))]
    [DisplayName("空间 Sigma（像素）"), Description("越大越重视较远的邻域像素；邻域直径仍限制最大范围。"), Range(0.01, 1000)]
    public double SigmaSpace { get; init; } = 5;

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Directional))]
    [DisplayName("取样方向"), Description("横向在同一行取样，纵向在同一列取样。")]
    public FilterSamplingDirection SamplingDirection { get; init; }

    [AppliesWhen(nameof(Method), nameof(ImageFilterMethod.Mean), nameof(ImageFilterMethod.BoxSum),
        nameof(ImageFilterMethod.Gaussian), nameof(ImageFilterMethod.Bilateral), nameof(ImageFilterMethod.Directional))]
    [DisplayName("边界扩展"), Description("全图模式以图边为界；ROI 模式以 ROI 边为界。支持补零、复制、镜像（含边像素）及镜像（不含边像素）。")]
    public GaussianBorderType BorderType { get; init; } = GaussianBorderType.Reflect101;

    [DisplayName("局部滤波 ROI"), Description("可接矩形 ROI 端口；也可填写轴对齐整像素范围。输出仅包含框内像素并保留坐标原点。")]
    public RotatedRectRoi? Region { get; init; }

    [DisplayName("处理像素上限"), Description("最大 1 亿像素；邻域和及双边模式另有工作量上限。"), Range(1, 100_000_000)]
    public int MaximumPixels { get; init; } = 100_000_000;
}

public static class ImageFilterProcessor
{
    private const int TileSide = 512;
    private const int BilateralMaximumWidth = 65_536;
    private const long BilateralMaximumPixels = 8_000_000;
    private const long BilateralWorkBudget = 300_000_000;

    public static IReadOnlyList<string> Validate(ImageFilterOptions p)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(p.Method)) errors.Add("请选择有效的滤波方式。");
        if (p.MaximumPixels is < 1 or > 100_000_000) errors.Add("处理像素上限必须为 1–100000000。");
        if (p.Region is { } region)
            try { _ = Rectangle(region); } catch (ArgumentException ex) { errors.Add(ex.Message); }
        if (p.Method != ImageFilterMethod.Median && !Enum.IsDefined(p.BorderType)) errors.Add("请选择有效的边界扩展。");
        if (p.Method is ImageFilterMethod.Mean or ImageFilterMethod.BoxSum)
        {
            if (!OddKernel(p.KernelWidth) || !OddKernel(p.KernelHeight)) errors.Add("横向核宽及纵向核高必须为 1–255 的正奇数。");
        }
        if (p.Method is ImageFilterMethod.Gaussian or ImageFilterMethod.Median or ImageFilterMethod.Directional)
        {
            if (!OddKernel(p.KernelSize)) errors.Add("核大小／取样长度必须为 1–255 的正奇数。");
        }
        if (p.Method is ImageFilterMethod.Gaussian && (!double.IsFinite(p.SigmaX) || p.SigmaX < 0 || !double.IsFinite(p.SigmaY) || p.SigmaY < 0))
            errors.Add("高斯 Sigma X、Y 必须是有限非负数。");
        if (p.Method is ImageFilterMethod.Directional && !Enum.IsDefined(p.SamplingDirection)) errors.Add("请选择有效的取样方向。");
        if (p.Method is ImageFilterMethod.Bilateral &&
            (p.Diameter is < 3 or > 31 || p.Diameter % 2 == 0 ||
            !double.IsFinite(p.SigmaColor) || p.SigmaColor is < 0.01 or > 1000 ||
            !double.IsFinite(p.SigmaSpace) || p.SigmaSpace is < 0.01 or > 1000))
            errors.Add("双边滤波直径必须为 3–31 的正奇数，两个 Sigma 均须为 0.01–1000 的有限值。");
        return errors;
    }

    public static ImageFrame Process(ImageFrame image, ImageFilterOptions options, RotatedRectRoi? roi = null, CancellationToken ct = default)
    {
        if (options.Method == ImageFilterMethod.BoxSum) throw new ArgumentException("方框邻域和使用 Float32 数值输出。");
        using var view = Open(image, options, roi, ct);
        using var builder = new ImageFrameBuilder(view.Width, view.Height, view.PixelFormat, coordinateSpace: view.CoordinateSpace);
        using var output = Mat.FromPixelData(view.Height, view.Width,
            view.PixelFormat == PixelFormat.Mono8 ? MatType.CV_8UC1 : MatType.CV_8UC3, builder.DataPointer, builder.Stride);
        FilterTiles(view, output, options, ct);
        return builder.Build(view.FrameId, view.Timestamp);
    }

    public static FloatPlaneFrame Sum(ImageFrame image, ImageFilterOptions options, RotatedRectRoi? roi = null, CancellationToken ct = default)
    {
        if (options.Method != ImageFilterMethod.BoxSum) throw new ArgumentException("请选择方框邻域和模式。");
        if (image.PixelFormat != PixelFormat.Mono8) throw new ArgumentException("方框邻域和只支持 Mono8，请显式转换。");
        using var view = Open(image, options, roi, ct);
        using var builder = new FloatPlaneBuilder(view.Width, view.Height);
        using var output = Mat.FromPixelData(view.Height, view.Width, MatType.CV_32FC1, builder.DataPointer, builder.Stride);
        FilterTiles(view, output, options, ct);
        return builder.Build(view.FrameId, view.Timestamp, view.CoordinateSpace, "neighborhood-sum", "intensity");
    }

    private static bool OddKernel(int size) => size is >= 1 and <= 255 && size % 2 == 1;

    private static RectRoi Rectangle(RotatedRectRoi roi)
    {
        roi.Validate();
        if (Math.Abs(Math.IEEERemainder(roi.AngleDegrees, 180)) > 1e-8)
            throw new ArgumentException("滤波 ROI 必须轴对齐。旋转区域请先显式旋转裁切。");
        return new(roi.CenterX - roi.Width / 2, roi.CenterY - roi.Height / 2, roi.Width, roi.Height);
    }

    private static ImageFrame Open(ImageFrame image, ImageFilterOptions options, RotatedRectRoi? roi, CancellationToken ct)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));
        ct.ThrowIfCancellationRequested();
        roi ??= options.Region;
        if (roi is not null && roi.CoordinateSpaceId != image.CoordinateSpace.Id)
            throw new ArgumentException("滤波 ROI 与图像坐标空间不同。");
        var view = ProcessingFrame.View(image, roi is null ? null : Rectangle(roi));
        var pixels = (long)view.Width * view.Height;
        string? error = null;
        if (pixels > options.MaximumPixels) error = "图像超出滤波像素预算，请选择较小的 ROI。";
        else if (options.Method == ImageFilterMethod.BoxSum && pixels > FloatPlaneBuilder.MaximumPixels)
            error = "方框邻域和最多处理 1600 万像素，请选择较小的 ROI。";
        else if (options.Method == ImageFilterMethod.Bilateral &&
            (view.Width > BilateralMaximumWidth || pixels > BilateralMaximumPixels ||
             pixels * options.Diameter * options.Diameter * view.PixelFormat.BytesPerPixel() > BilateralWorkBudget))
            error = "超出双边滤波工作量预算，请减小 ROI 或邻域直径。";
        if (error is null) return view;
        view.Dispose();
        throw new ArgumentException(error);
    }

    private static void FilterTiles(ImageFrame view, Mat output, ImageFilterOptions p, CancellationToken ct)
    {
        var kernel = p.Method switch
        {
            ImageFilterMethod.Mean or ImageFilterMethod.BoxSum => new Size(p.KernelWidth, p.KernelHeight),
            ImageFilterMethod.Directional when p.SamplingDirection == FilterSamplingDirection.Horizontal => new Size(p.KernelSize, 1),
            ImageFilterMethod.Directional => new Size(1, p.KernelSize),
            ImageFilterMethod.Bilateral => new Size(p.Diameter, p.Diameter),
            _ => new Size(p.KernelSize, p.KernelSize)
        };
        var gaussian = new GaussianBlurOptions { KernelSize = p.KernelSize, SigmaX = p.SigmaX, SigmaY = p.SigmaY, BorderType = p.BorderType };
        var border = p.Method == ImageFilterMethod.Median ? BorderTypes.Replicate : GaussianBlurProcessor.ToBorder(p.BorderType);
        // Bilateral's SIMD and scalar tails can round a Mono8 pixel differently. Preserve the whole
        // row's native vector lane and tail alignment by splitting only along Y for that method.
        var tileWidth = p.Method == ImageFilterMethod.Bilateral ? view.Width : TileSide;
        var tileHeight = p.Method == ImageFilterMethod.Bilateral
            ? Math.Clamp(TileSide * TileSide / view.Width, 1, TileSide)
            : TileSide;
        for (var y = 0; y < view.Height; y += tileHeight)
        for (var x = 0; x < view.Width; x += tileWidth)
        {
            ct.ThrowIfCancellationRequested();
            var w = Math.Min(tileWidth, view.Width - x);
            var h = Math.Min(tileHeight, view.Height - y);
            var left = Math.Max(0, x - kernel.Width / 2);
            var top = Math.Max(0, y - kernel.Height / 2);
            var right = Math.Min(view.Width, x + w + kernel.Width / 2);
            var bottom = Math.Min(view.Height, y + h + kernel.Height / 2);
            using var tile = view.CreateView(left, top, right - left, bottom - top);
            using var input = new OpenCvImageLease(tile);
            using var filtered = new Mat();
            switch (p.Method)
            {
                case ImageFilterMethod.Gaussian:
                    GaussianBlurProcessor.Apply(input.Mat, filtered, gaussian);
                    break;
                case ImageFilterMethod.Median:
                    MedianBlurProcessor.Apply(input.Mat, filtered, p.KernelSize);
                    break;
                case ImageFilterMethod.Bilateral:
                    Cv2.BilateralFilter(input.Mat, filtered, p.Diameter, p.SigmaColor, p.SigmaSpace, border);
                    break;
                default:
                    var sum = p.Method == ImageFilterMethod.BoxSum;
                    Cv2.BoxFilter(input.Mat, filtered, sum ? MatType.CV_32FC1 : new MatType(-1), kernel, normalize: !sum, borderType: border);
                    break;
            }
            ct.ThrowIfCancellationRequested();
            using var result = new Mat(filtered, new Rect(x - left, y - top, w, h));
            using var destination = new Mat(output, new Rect(x, y, w, h));
            result.CopyTo(destination);
        }
        ct.ThrowIfCancellationRequested();
    }
}

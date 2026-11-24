using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Transforms;

public enum RotationOutputSize { KeepSize, ExpandToFit }
public enum ResizeImageMode { Dimensions, Scale }
public enum ResizeInterpolation { Nearest, Linear, Cubic, Area, Lanczos4 }
public enum FlipDirection { Horizontal, Vertical, Both }

public sealed record RotateImageOptions
{
    [DisplayName("顺时针旋转角度（°）")] public double Angle { get; init; }
    [DisplayName("旋转中心 X（原图像素中心）")] public double? CenterX { get; init; }
    [DisplayName("旋转中心 Y（原图像素中心）")] public double? CenterY { get; init; }
    [DisplayName("插值方式")] public CropInterpolation Interpolation { get; init; } = CropInterpolation.Linear;
    [DisplayName("边界填充方式")] public CropBorderMode BorderMode { get; init; } = CropBorderMode.Constant;
    [DisplayName("常量边界值"), Range(0, 255)] public byte BorderValue { get; init; }
    [DisplayName("输出尺寸策略")] public RotationOutputSize OutputSize { get; init; } = RotationOutputSize.KeepSize;
}

public sealed record ResizeImageOptions
{
    [DisplayName("缩放方式")] public ResizeImageMode Mode { get; init; } = ResizeImageMode.Dimensions;
    [DisplayName("目标宽度"), Range(1, 32766)] public int Width { get; init; } = 100;
    [DisplayName("目标高度"), Range(1, 32766)] public int Height { get; init; } = 100;
    [DisplayName("水平缩放比例"), Range(0.000001, 32766)] public double ScaleX { get; init; } = 1;
    [DisplayName("垂直缩放比例"), Range(0.000001, 32766)] public double ScaleY { get; init; } = 1;
    [DisplayName("插值方式")] public ResizeInterpolation Interpolation { get; init; } = ResizeInterpolation.Linear;
}

public sealed record FlipImageOptions
{
    [DisplayName("翻转方向")] public FlipDirection Direction { get; init; } = FlipDirection.Horizontal;
}

/// <summary>Caller owns Image. Transform maps local output pixel-center indices to input original coordinates.</summary>
public sealed record ImageTransformOutput(ImageFrame Image, CoordinateTransform2D CoordinateTransform);

internal static class TransformFrame
{
    // Bound deliberate output allocation. These are safety limits, not a total process-memory budget.
    public const int MaximumDimension = 32766;
    public const long MaximumOutputBytes = 512L * 1024 * 1024;
    public static void ValidateInput(ImageFrame image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PixelFormat.IsBayer()) throw new NotSupportedException(
            "Image transforms require explicit BayerRG8/BayerGB8 conversion to RGB8/BGR8; interpolation or flipping must not silently change mosaic semantics.");
    }
    public static void ValidateDimensions(int width, int height, PixelFormat format)
    {
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension ||
            (long)width * height * format.BytesPerPixel() > MaximumOutputBytes)
            throw new ArgumentException("Transform output must have dimensions 1..32766 and pixel payload at most 512 MiB.");
    }
    public static ImageCoordinateSpace NewSpace() => new("transform:" + Guid.NewGuid().ToString("N"));
    public static Mat Target(ImageFrameBuilder builder) => Mat.FromPixelData(builder.Height, builder.Width,
        builder.PixelFormat == PixelFormat.Mono8 ? MatType.CV_8UC1 : MatType.CV_8UC3, builder.DataPointer, builder.Stride);
    public static void ThrowInvalid(IReadOnlyList<string> errors)
    { if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors)); }
}

public static class RotateImageProcessor
{
    public static IReadOnlyList<string> Validate(RotateImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!double.IsFinite(options.Angle)) errors.Add("Angle must be finite.");
        if (options.CenterX.HasValue != options.CenterY.HasValue) errors.Add("CenterX and CenterY must both be provided, or both be automatic.");
        if (options.CenterX is { } x && !double.IsFinite(x) || options.CenterY is { } y && !double.IsFinite(y)) errors.Add("Rotation center must be finite.");
        if (!Enum.IsDefined(options.Interpolation)) errors.Add("Rotation interpolation is invalid.");
        if (!Enum.IsDefined(options.BorderMode)) errors.Add("Rotation border mode is invalid.");
        if (!Enum.IsDefined(options.OutputSize)) errors.Add("Rotation output size strategy is invalid.");
        return errors;
    }

    /// <summary>Positive angles visually rotate the source clockwise. Existing ROI types are not changed.
    /// ExpandToFit rotates pixel boundaries [-.5,w-.5]×[-.5,h-.5] and translates their enclosing canvas.
    /// CoordinateTransform is the inverse sampling map, including the input origin once.</summary>
    public static ImageTransformOutput Process(ImageFrame image, RotateImageOptions options, CancellationToken ct = default)
    {
        TransformFrame.ValidateInput(image); TransformFrame.ThrowInvalid(Validate(options)); ct.ThrowIfCancellationRequested();
        var centerX = (options.CenterX ?? image.CoordinateSpace.OriginX + (image.Width - 1) / 2.0) - image.CoordinateSpace.OriginX;
        var centerY = (options.CenterY ?? image.CoordinateSpace.OriginY + (image.Height - 1) / 2.0) - image.CoordinateSpace.OriginY;
        var radians = (options.Angle % 360) * Math.PI / 180;
        var c = Snap(Math.Cos(radians)); var s = Snap(Math.Sin(radians));
        var width = image.Width; var height = image.Height; var canvasX = 0.0; var canvasY = 0.0;
        if (options.OutputSize == RotationOutputSize.ExpandToFit)
        {
            var corners = new[] { (-.5, -.5), (image.Width - .5, -.5), (image.Width - .5, image.Height - .5), (-.5, image.Height - .5) }
                .Select(p => new Point2D(c * (p.Item1 - centerX) - s * (p.Item2 - centerY) + centerX,
                    s * (p.Item1 - centerX) + c * (p.Item2 - centerY) + centerY)).ToArray();
            var minX = corners.Min(p => p.X); var minY = corners.Min(p => p.Y);
            width = CanvasDimension(corners.Max(p => p.X) - minX); height = CanvasDimension(corners.Max(p => p.Y) - minY);
            canvasX = minX + .5; canvasY = minY + .5;
        }
        TransformFrame.ValidateDimensions(width, height, image.PixelFormat);
        var offsetX = c * (canvasX - centerX) + s * (canvasY - centerY) + centerX;
        var offsetY = -s * (canvasX - centerX) + c * (canvasY - centerY) + centerY;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || Math.Abs(offsetX) > int.MaxValue / 2048.0 || Math.Abs(offsetY) > int.MaxValue / 2048.0)
            throw new ArgumentException("Rotation center produces a sampling offset outside the supported native coordinate range.");
        var space = TransformFrame.NewSpace();
        var mapping = new CoordinateTransform2D(c, s, -s, c, offsetX + image.CoordinateSpace.OriginX,
            offsetY + image.CoordinateSpace.OriginY, space.Id, image.CoordinateSpace.Id);
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, c); matrix.Set(0, 1, s); matrix.Set(0, 2, offsetX);
        matrix.Set(1, 0, -s); matrix.Set(1, 1, c); matrix.Set(1, 2, offsetY);
        using var source = new OpenCvImageLease(image);
        using var builder = new ImageFrameBuilder(width, height, image.PixelFormat, coordinateSpace: space);
        using var target = TransformFrame.Target(builder);
        Cv2.WarpAffine(source.Mat, target, matrix, new Size(width, height), Interpolation(options.Interpolation) | InterpolationFlags.WarpInverseMap,
            Border(options.BorderMode), Scalar.All(options.BorderValue));
        ct.ThrowIfCancellationRequested();
        return new(builder.Build(image.FrameId, image.Timestamp), mapping);
    }
    private static double Snap(double value) => Math.Abs(value) < 1e-14 ? 0 : Math.Abs(1 - Math.Abs(value)) < 1e-14 ? Math.Sign(value) : value;
    private static int CanvasDimension(double value)
    {
        if (!double.IsFinite(value) || value <= 0 || value > TransformFrame.MaximumDimension) throw new ArgumentException("Expanded rotation exceeds output dimension limit.");
        return checked((int)Math.Ceiling(value - Math.Max(1e-10, value * 1e-12)));
    }
    private static InterpolationFlags Interpolation(CropInterpolation value) => value switch
    {
        CropInterpolation.Nearest => InterpolationFlags.Nearest, CropInterpolation.Linear => InterpolationFlags.Linear,
        CropInterpolation.Cubic => InterpolationFlags.Cubic, CropInterpolation.Lanczos4 => InterpolationFlags.Lanczos4,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
    private static BorderTypes Border(CropBorderMode value) => value switch
    {
        CropBorderMode.Constant => BorderTypes.Constant, CropBorderMode.Replicate => BorderTypes.Replicate,
        CropBorderMode.Reflect => BorderTypes.Reflect, CropBorderMode.Reflect101 => BorderTypes.Reflect101, CropBorderMode.Wrap => BorderTypes.Wrap,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public static class ResizeImageProcessor
{
    public static IReadOnlyList<string> Validate(ResizeImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode)) errors.Add("Resize mode is invalid.");
        if (!Enum.IsDefined(options.Interpolation)) errors.Add("Resize interpolation is invalid.");
        if (options.Mode == ResizeImageMode.Dimensions && (options.Width is < 1 or > TransformFrame.MaximumDimension || options.Height is < 1 or > TransformFrame.MaximumDimension))
            errors.Add("Resize dimensions must be in [1,32766].");
        if (options.Mode == ResizeImageMode.Scale && (!double.IsFinite(options.ScaleX) || !double.IsFinite(options.ScaleY) || options.ScaleX <= 0 || options.ScaleY <= 0))
            errors.Add("Resize scale factors must be finite and positive.");
        return errors;
    }
    public static ImageTransformOutput Process(ImageFrame image, ResizeImageOptions options, CancellationToken ct = default)
    {
        TransformFrame.ValidateInput(image); TransformFrame.ThrowInvalid(Validate(options)); ct.ThrowIfCancellationRequested();
        var width = options.Mode == ResizeImageMode.Dimensions ? options.Width : ScaledDimension(image.Width, options.ScaleX);
        var height = options.Mode == ResizeImageMode.Dimensions ? options.Height : ScaledDimension(image.Height, options.ScaleY);
        TransformFrame.ValidateDimensions(width, height, image.PixelFormat);
        var ratioX = (double)image.Width / width; var ratioY = (double)image.Height / height;
        var space = TransformFrame.NewSpace();
        var mapping = new CoordinateTransform2D(ratioX, 0, 0, ratioY,
            image.CoordinateSpace.OriginX + .5 * ratioX - .5, image.CoordinateSpace.OriginY + .5 * ratioY - .5, space.Id, image.CoordinateSpace.Id);
        var interpolation = options.Interpolation switch
        {
            // OpenCvSharp 4.13 omits the named member; OpenCV's stable INTER_NEAREST_EXACT value is 6.
            ResizeInterpolation.Nearest => (InterpolationFlags)6, ResizeInterpolation.Linear => InterpolationFlags.Linear,
            ResizeInterpolation.Cubic => InterpolationFlags.Cubic, ResizeInterpolation.Area => InterpolationFlags.Area,
            ResizeInterpolation.Lanczos4 => InterpolationFlags.Lanczos4, _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        using var source = new OpenCvImageLease(image);
        using var builder = new ImageFrameBuilder(width, height, image.PixelFormat, coordinateSpace: space);
        using var target = TransformFrame.Target(builder);
        Cv2.Resize(source.Mat, target, new Size(width, height), 0, 0, interpolation);
        ct.ThrowIfCancellationRequested();
        return new(builder.Build(image.FrameId, image.Timestamp), mapping);
    }
    private static int ScaledDimension(int input, double scale)
    {
        var requested = input * scale;
        if (!double.IsFinite(requested) || requested < .5 || requested > TransformFrame.MaximumDimension)
            throw new ArgumentException("Scaled dimensions must round to a value in [1,32766].");
        return checked((int)Math.Round(requested, MidpointRounding.AwayFromZero));
    }
}

public static class FlipImageProcessor
{
    public static IReadOnlyList<string> Validate(FlipImageOptions options)
    { ArgumentNullException.ThrowIfNull(options); return Enum.IsDefined(options.Direction) ? [] : ["Flip direction is invalid."]; }
    public static ImageTransformOutput Process(ImageFrame image, FlipImageOptions options, CancellationToken ct = default)
    {
        TransformFrame.ValidateInput(image); TransformFrame.ThrowInvalid(Validate(options)); ct.ThrowIfCancellationRequested();
        TransformFrame.ValidateDimensions(image.Width, image.Height, image.PixelFormat);
        var horizontal = options.Direction is FlipDirection.Horizontal or FlipDirection.Both;
        var vertical = options.Direction is FlipDirection.Vertical or FlipDirection.Both;
        var space = TransformFrame.NewSpace();
        var mapping = new CoordinateTransform2D(horizontal ? -1 : 1, 0, 0, vertical ? -1 : 1,
            image.CoordinateSpace.OriginX + (horizontal ? image.Width - 1 : 0), image.CoordinateSpace.OriginY + (vertical ? image.Height - 1 : 0), space.Id, image.CoordinateSpace.Id);
        using var source = new OpenCvImageLease(image);
        using var builder = new ImageFrameBuilder(image.Width, image.Height, image.PixelFormat, coordinateSpace: space);
        using var target = TransformFrame.Target(builder);
        Cv2.Flip(source.Mat, target, horizontal ? vertical ? FlipMode.XY : FlipMode.Y : FlipMode.X);
        ct.ThrowIfCancellationRequested();
        return new(builder.Build(image.FrameId, image.Timestamp), mapping);
    }
}

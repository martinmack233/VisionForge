using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Transforms;

public sealed record CropImageOptions
{
    [DisplayName("左上角 X")] public int X { get; init; }
    [DisplayName("左上角 Y")] public int Y { get; init; }
    [DisplayName("宽度"), Range(1, int.MaxValue)] public int Width { get; init; } = 100;
    [DisplayName("高度"), Range(1, int.MaxValue)] public int Height { get; init; } = 100;
}

public enum CropInterpolation { Nearest, Linear, Cubic, Lanczos4 }
public enum CropBorderMode { Constant, Replicate, Reflect, Reflect101, Wrap }

/// <summary>CenterX/Y are pixel-center indices. A connected Pose2D overrides center/angle,
/// and scales Width/Height. A connected existing ROI instead supplies all geometry.</summary>
public sealed record RotatedCropOptions
{
    [DisplayName("中心 X")] public double CenterX { get; init; } = 49.5;
    [DisplayName("中心 Y")] public double CenterY { get; init; } = 49.5;
    [DisplayName("宽度"), Range(1, int.MaxValue)] public int Width { get; init; } = 100;
    [DisplayName("高度"), Range(1, int.MaxValue)] public int Height { get; init; } = 100;
    [DisplayName("角度（顺时针）")] public double Angle { get; init; }
    [DisplayName("插值")] public CropInterpolation Interpolation { get; init; } = CropInterpolation.Linear;
    [DisplayName("越界填充")] public CropBorderMode BorderMode { get; init; } = CropBorderMode.Constant;
    [DisplayName("常量填充值"), Range(0, 255)] public byte BorderValue { get; init; }
}

/// <summary>The caller owns Image. Transform maps its local pixel-center indices into the input space.</summary>
public sealed record CropOutput(ImageFrame Image, CoordinateTransform2D CoordinateTransform);

public static class CropImageProcessor
{
    public static IReadOnlyList<string> Validate(CropImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Width <= 0 || options.Height <= 0 ? ["Crop width and height must be positive."] : [];
    }

    /// <summary>Rectangle uses existing ROI boundary coordinates. It must be integer-aligned and wholly inside
    /// the image. Only the crop is copied, respecting stride; input is immutable.</summary>
    public static CropOutput Process(ImageFrame image, CropImageOptions options, RectRoi? rectangle = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        RejectBayer(image);
        ct.ThrowIfCancellationRequested();
        rectangle ??= new RectRoi(options.X, options.Y, options.Width, options.Height);
        var x = Integer(rectangle.X - image.CoordinateSpace.OriginX, "CropRectangle.X");
        var y = Integer(rectangle.Y - image.CoordinateSpace.OriginY, "CropRectangle.Y");
        var width = Integer(rectangle.Width, "CropRectangle.Width");
        var height = Integer(rectangle.Height, "CropRectangle.Height");
        using var view = image.CreateView(x, y, width, height);
        var space = NewSpace();
        var transform = new CoordinateTransform2D(1, 0, 0, 1, rectangle.X, rectangle.Y, space.Id, image.CoordinateSpace.Id);
        using var input = view.Lease();
        using var builder = new ImageFrameBuilder(width, height, image.PixelFormat, coordinateSpace: space);
        for (var row = 0; row < height; row++)
        {
            ct.ThrowIfCancellationRequested();
            input.GetRowSpan(row).CopyTo(builder.GetRowSpan(row));
        }
        ct.ThrowIfCancellationRequested();
        return new CropOutput(builder.Build(image.FrameId, image.Timestamp), transform);
    }

    internal static void RejectBayer(ImageFrame image)
    {
        if (image.PixelFormat.IsBayer()) throw new NotSupportedException(
            "Crop nodes require explicit BayerRG8/BayerGB8 to RGB8/BGR8 conversion; a new local image must not lose Bayer phase metadata.");
    }

    internal static ImageCoordinateSpace NewSpace() => new("crop:" + Guid.NewGuid().ToString("N"));

    private static int Integer(double value, string name)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue || Math.Abs(value - Math.Round(value)) > 1e-9)
            throw new ArgumentException(name + " must be a finite integer pixel boundary.");
        return checked((int)Math.Round(value));
    }
}

public static class RotatedCropProcessor
{
    public static IReadOnlyList<string> Validate(RotatedCropOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (options.Width <= 0 || options.Height <= 0) errors.Add("Crop width and height must be positive.");
        if (!double.IsFinite(options.CenterX) || !double.IsFinite(options.CenterY) || !double.IsFinite(options.Angle))
            errors.Add("Crop center and angle must be finite.");
        if (!Enum.IsDefined(options.Interpolation)) errors.Add("Unsupported crop interpolation.");
        if (!Enum.IsDefined(options.BorderMode)) errors.Add("Unsupported crop border mode.");
        return errors;
    }

    /// <summary>Inverse samples the source around the requested center. Positive angle follows the source's
    /// clockwise orientation. ROI centers describe geometric pixel boundaries, so subtract (0.5,0.5) to obtain
    /// a pixel-center index. Pose2D already uses pixel-center indices and needs no half-pixel conversion.
    /// ROI and Pose2D together are ambiguous and rejected. Out-of-image samples use BorderMode.</summary>
    public static CropOutput Process(ImageFrame image, RotatedCropOptions options, RotatedRectRoi? roi = null,
        Pose2D? pose = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        var errors = Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        CropImageProcessor.RejectBayer(image);
        if (roi is not null && pose is not null) throw new ArgumentException("Connect ROI or Pose2D, not both. Use ROITransform to combine reference ROI and template pose.");
        ct.ThrowIfCancellationRequested();
        var centerX = options.CenterX; var centerY = options.CenterY; var angle = options.Angle;
        double requestedWidth = options.Width; double requestedHeight = options.Height;
        if (roi is not null)
        {
            roi.Validate();
            RequireSpace(roi.CoordinateSpaceId, image);
            centerX = roi.CenterX - 0.5; centerY = roi.CenterY - 0.5; angle = roi.AngleDegrees;
            requestedWidth = roi.Width; requestedHeight = roi.Height;
        }
        if (pose is not null)
        {
            RequireSpace(pose.CoordinateSpaceId, image);
            if (!double.IsFinite(pose.CenterX) || !double.IsFinite(pose.CenterY) || !double.IsFinite(pose.AngleDegrees) ||
                !double.IsFinite(pose.Scale) || pose.Scale <= 0) throw new ArgumentException("Pose must be finite with positive scale.", nameof(pose));
            centerX = pose.CenterX; centerY = pose.CenterY; angle = pose.AngleDegrees;
            requestedWidth *= pose.Scale; requestedHeight *= pose.Scale;
        }
        var width = Dimension(requestedWidth); var height = Dimension(requestedHeight);
        // Reduce finite angles before conversion, avoiding radians overflow for otherwise valid inputs.
        var radians = (angle % 360) * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        var outputCenterX = (width - 1) / 2.0; var outputCenterY = (height - 1) / 2.0;
        var offsetX = centerX - c * outputCenterX + s * outputCenterY;
        var offsetY = centerY - s * outputCenterX - c * outputCenterY;
        var space = CropImageProcessor.NewSpace();
        var transform = new CoordinateTransform2D(c, -s, s, c, offsetX, offsetY, space.Id, image.CoordinateSpace.Id);
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, c); matrix.Set(0, 1, -s); matrix.Set(0, 2, offsetX - image.CoordinateSpace.OriginX);
        matrix.Set(1, 0, s); matrix.Set(1, 1, c); matrix.Set(1, 2, offsetY - image.CoordinateSpace.OriginY);
        using var source = new OpenCvImageLease(image);
        using var builder = new ImageFrameBuilder(width, height, image.PixelFormat, coordinateSpace: space);
        using var target = Mat.FromPixelData(height, width, image.PixelFormat.BytesPerPixel() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3,
            builder.DataPointer, builder.Stride);
        var interpolation = options.Interpolation switch
        {
            CropInterpolation.Nearest => InterpolationFlags.Nearest,
            CropInterpolation.Linear => InterpolationFlags.Linear,
            CropInterpolation.Cubic => InterpolationFlags.Cubic,
            CropInterpolation.Lanczos4 => InterpolationFlags.Lanczos4,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        var border = options.BorderMode switch
        {
            CropBorderMode.Constant => BorderTypes.Constant,
            CropBorderMode.Replicate => BorderTypes.Replicate,
            CropBorderMode.Reflect => BorderTypes.Reflect,
            CropBorderMode.Reflect101 => BorderTypes.Reflect101,
            CropBorderMode.Wrap => BorderTypes.Wrap,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        Cv2.WarpAffine(source.Mat, target, matrix, new Size(width, height), interpolation | InterpolationFlags.WarpInverseMap,
            border, Scalar.All(options.BorderValue));
        ct.ThrowIfCancellationRequested();
        return new CropOutput(builder.Build(image.FrameId, image.Timestamp), transform);
    }

    private static int Dimension(double value)
    {
        if (!double.IsFinite(value) || value < 0.5 || value > int.MaxValue)
            throw new ArgumentException("Scaled crop dimensions must round to a positive Int32 pixel count.");
        return checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
    }

    private static void RequireSpace(string id, ImageFrame image)
    {
        if (id != image.CoordinateSpace.Id) throw new ArgumentException("Crop geometry and image must use the same coordinate space.");
    }
}

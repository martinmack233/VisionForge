using OpenCvSharp;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Imaging;

public sealed class ImageConversionResult(ImageFrame image, bool allocatedNewBuffer) : IDisposable
{
    public ImageFrame Image { get; } = image;
    public bool AllocatedNewBuffer { get; } = allocatedNewBuffer;
    public void Dispose() => Image.Dispose();
}

public static class PixelFormatConverter
{
    /// <summary>All real conversions allocate a new buffer. Same-format conversion returns a shared read-only frame.</summary>
    public static ImageConversionResult Convert(ImageFrame image, PixelFormat target)
    {
        ArgumentNullException.ThrowIfNull(image);
        _ = target.BytesPerPixel();
        if (image.PixelFormat == target) return new ImageConversionResult(image.Share(), false);
        if (target.IsBayer()) throw new NotSupportedException("Synthesizing a raw Bayer sensor image is not a supported conversion.");
        if (image.PixelFormat.IsBayer() && target == PixelFormat.Mono8)
            throw new NotSupportedException("Convert Bayer to RGB8/BGR8 explicitly before grayscale processing.");
        using var input = new OpenCvImageLease(image);
        using var builder = new ImageFrameBuilder(image.Width, image.Height, target, coordinateSpace: image.CoordinateSpace);
        using var output = Mat.FromPixelData(image.Height, image.Width, target.BytesPerPixel() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3, builder.DataPointer, builder.Stride);
        var code = image.PixelFormat switch
        {
            PixelFormat.Mono8 when target is PixelFormat.RGB8 or PixelFormat.BGR8 => ColorConversionCodes.GRAY2BGR,
            PixelFormat.RGB8 when target == PixelFormat.BGR8 => ColorConversionCodes.RGB2BGR,
            PixelFormat.BGR8 when target == PixelFormat.RGB8 => ColorConversionCodes.BGR2RGB,
            PixelFormat.RGB8 when target == PixelFormat.Mono8 => ColorConversionCodes.RGB2GRAY,
            PixelFormat.BGR8 when target == PixelFormat.Mono8 => ColorConversionCodes.BGR2GRAY,
            PixelFormat.BayerRG8 or PixelFormat.BayerGB8 => BayerCode(image, target),
            _ => throw new NotSupportedException($"Conversion {image.PixelFormat} -> {target} is unsupported.")
        };
        Cv2.CvtColor(input.Mat, output, code);
        return new ImageConversionResult(builder.Build(image.FrameId, image.Timestamp), true);
    }

    private static ColorConversionCodes BayerCode(ImageFrame image, PixelFormat target)
    {
        // OpenCV's short names refer to the opposite corner. Physical RGGB -> BayerBG2BGR.
        // Track cropping phase independently from geometry; odd-offset ROIs must not swap red and blue.
        var phase = image.PixelFormat == PixelFormat.BayerRG8 ? 0 : 2; // RGGB=0, GRBG=1, GBRG=2, BGGR=3
        phase ^= image.MosaicOffsetX;
        phase ^= image.MosaicOffsetY * 2;
        return (phase, target) switch
        {
            (0, PixelFormat.BGR8) => ColorConversionCodes.BayerBG2BGR,
            (0, PixelFormat.RGB8) => ColorConversionCodes.BayerBG2RGB,
            (1, PixelFormat.BGR8) => ColorConversionCodes.BayerGB2BGR,
            (1, PixelFormat.RGB8) => ColorConversionCodes.BayerGB2RGB,
            (2, PixelFormat.BGR8) => ColorConversionCodes.BayerGR2BGR,
            (2, PixelFormat.RGB8) => ColorConversionCodes.BayerGR2RGB,
            (3, PixelFormat.BGR8) => ColorConversionCodes.BayerRG2BGR,
            (3, PixelFormat.RGB8) => ColorConversionCodes.BayerRG2RGB,
            _ => throw new NotSupportedException()
        };
    }
}

using OpenCvSharp;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Imaging;

public static class ImageFile
{
    /// <summary>Decode with original depth; rejects unsupported 16-bit/alpha images. Raw Bayer requires an explicit format.</summary>
    public static ImageFrame Load(string path, PixelFormat? desired = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Image resource is missing.", path);
        using var decoded = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new InvalidDataException($"Unable to decode image: {path}");
        if (decoded.Depth() != MatType.CV_8U || decoded.Channels() is not (1 or 3))
            throw new NotSupportedException("Only 8-bit mono or three-channel images are supported.");
        if (desired is PixelFormat.BayerRG8 or PixelFormat.BayerGB8)
        {
            if (decoded.Channels() != 1) throw new InvalidDataException("Raw Bayer data must be stored as one channel.");
            return FrameFactory.CopyFromMat(decoded, desired.Value);
        }
        var sourceFormat = decoded.Channels() == 1 ? PixelFormat.Mono8 : PixelFormat.BGR8;
        using var source = FrameFactory.CopyFromMat(decoded, sourceFormat);
        if (desired is null || desired == sourceFormat) return source.Share();
        using var conversion = PixelFormatConverter.Convert(source, desired.Value);
        return conversion.Image.Share();
    }

    /// <summary>RGB is encoded with correct channel order. Bayer is saved as the original single-channel mosaic.</summary>
    public static void Save(ImageFrame image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent is not null) Directory.CreateDirectory(parent);
        if (image.PixelFormat == PixelFormat.RGB8)
        {
            using var converted = PixelFormatConverter.Convert(image, PixelFormat.BGR8);
            using var pixels = new OpenCvImageLease(converted.Image);
            if (!Cv2.ImWrite(path, pixels.Mat)) throw new IOException($"Image save failed: {path}");
        }
        else
        {
            using var pixels = new OpenCvImageLease(image);
            if (!Cv2.ImWrite(path, pixels.Mat)) throw new IOException($"Image save failed: {path}");
        }
    }
}

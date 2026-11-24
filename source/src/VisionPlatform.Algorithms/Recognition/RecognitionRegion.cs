using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.Recognition;

/// <summary>Recognition ROI uses existing geometry. Returns an owned reference, never disposes the borrowed input.</summary>
public static class RecognitionRegion
{
    public static ImageFrame Open(ImageFrame image, RotatedRectRoi? roi, long maximumPixels = 100_000_000)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (maximumPixels <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        if (roi is null)
        {
            if ((long)image.Width * image.Height > maximumPixels) throw new ArgumentException("Recognition image exceeds the configured pixel budget; specify an ROI.");
            return image.Share();
        }
        roi.Validate();
        if (roi.CoordinateSpaceId != image.CoordinateSpace.Id) throw new ArgumentException("Recognition ROI coordinate space differs from the image.");
        if (Math.Abs(Math.IEEERemainder(roi.AngleDegrees, 180)) > 1e-8)
            throw new ArgumentException("Recognition ROI must be axis aligned. Use RotatedCrop for a rotated ROI and retain its CoordinateTransform.");
        var left = roi.CenterX - roi.Width / 2 - image.CoordinateSpace.OriginX;
        var top = roi.CenterY - roi.Height / 2 - image.CoordinateSpace.OriginY;
        var right = left + roi.Width;
        var bottom = top + roi.Height;
        if (left < 0 || top < 0 || right > image.Width || bottom > image.Height)
            throw new ArgumentException("Recognition ROI must be entirely inside the image.");
        var x = checked((int)Math.Floor(left)); var y = checked((int)Math.Floor(top));
        var width = checked((int)Math.Ceiling(right) - x); var height = checked((int)Math.Ceiling(bottom) - y);
        if ((long)width * height > maximumPixels) throw new ArgumentException("Recognition ROI exceeds the configured pixel budget.");
        return image.CreateView(x, y, width, height);
    }
}

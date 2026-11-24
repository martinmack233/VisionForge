using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.ROI;

public static class RoiTransformer
{
    /// <summary>Maps a taught ROI from reference pose to current pose, including translation, clockwise rotation and scale.</summary>
    public static RotatedRectRoi Transform(RotatedRectRoi roi, Pose2D reference, Pose2D actual, ImageFrame image)
    {
        ArgumentNullException.ThrowIfNull(roi); ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(actual); ArgumentNullException.ThrowIfNull(image);
        roi.Validate(); ValidatePose(reference); ValidatePose(actual);
        if (roi.CoordinateSpaceId != reference.CoordinateSpaceId || reference.CoordinateSpaceId != actual.CoordinateSpaceId ||
            actual.CoordinateSpaceId != image.CoordinateSpace.Id)
            throw new ArgumentException("ROI, reference pose, current pose and image must use the same coordinate space.");
        var ratio = actual.Scale / reference.Scale;
        var delta = actual.AngleDegrees - reference.AngleDegrees;
        var radians = delta * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        var x = roi.CenterX - reference.CenterX; var y = roi.CenterY - reference.CenterY;
        var result = new RotatedRectRoi(actual.CenterX + ratio * (c * x - s * y),
            actual.CenterY + ratio * (s * x + c * y), roi.Width * ratio, roi.Height * ratio,
            roi.AngleDegrees + delta, image.CoordinateSpace.Id);
        ValidateInImage(result, image);
        return result;
    }

    public static void ValidateInImage(RotatedRectRoi roi, ImageFrame image)
    {
        ArgumentNullException.ThrowIfNull(roi); ArgumentNullException.ThrowIfNull(image);
        roi.Validate();
        if (roi.CoordinateSpaceId != image.CoordinateSpace.Id) throw new ArgumentException("ROI coordinate space does not match image.");
        const double epsilon = 1e-8;
        var ox = image.CoordinateSpace.OriginX; var oy = image.CoordinateSpace.OriginY;
        if (roi.Corners().Any(p => p.X < ox - epsilon || p.Y < oy - epsilon || p.X > ox + image.Width + epsilon || p.Y > oy + image.Height + epsilon))
            throw new ArgumentOutOfRangeException(nameof(roi), "The complete rotated ROI must be inside the input image; no clipping is applied.");
    }

    private static void ValidatePose(Pose2D pose)
    {
        if (new[] { pose.CenterX, pose.CenterY, pose.AngleDegrees, pose.Scale }.Any(v => !double.IsFinite(v)) || pose.Scale <= 0 ||
            string.IsNullOrWhiteSpace(pose.CoordinateSpaceId)) throw new ArgumentException("Pose must be finite with a positive scale and valid coordinate space.");
    }
}

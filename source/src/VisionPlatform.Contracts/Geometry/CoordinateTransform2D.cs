namespace VisionPlatform.Contracts.Geometry;

/// <summary>
/// Affine mapping of pixel-center indices, x right/y down. Positive rotation is clockwise.
/// Crop output (0,0) is the center of its first pixel. This maps output indices to source
/// indices; it is independent of display zoom and does not alter ImageFrame/ROI contracts.
/// </summary>
public sealed record CoordinateTransform2D(double M11, double M12, double M21, double M22,
    double OffsetX, double OffsetY, string SourceSpaceId = "crop", string TargetSpaceId = "image")
{
    public Point2D Map(Point2D point) => new(M11 * point.X + M12 * point.Y + OffsetX,
        M21 * point.X + M22 * point.Y + OffsetY);

    public CoordinateTransform2D Inverse()
    {
        var determinant = M11 * M22 - M12 * M21;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-15)
            throw new InvalidOperationException("Coordinate transform is not invertible.");
        var a = M22 / determinant; var b = -M12 / determinant;
        var c = -M21 / determinant; var d = M11 / determinant;
        return new(a, b, c, d, -a * OffsetX - b * OffsetY, -c * OffsetX - d * OffsetY,
            TargetSpaceId, SourceSpaceId);
    }

    /// <summary>Apply this transform, then the supplied transform.</summary>
    public CoordinateTransform2D Then(CoordinateTransform2D next)
    {
        if (TargetSpaceId != next.SourceSpaceId) throw new ArgumentException("Coordinate spaces do not connect.", nameof(next));
        return new(next.M11 * M11 + next.M12 * M21, next.M11 * M12 + next.M12 * M22,
            next.M21 * M11 + next.M22 * M21, next.M21 * M12 + next.M22 * M22,
            next.M11 * OffsetX + next.M12 * OffsetY + next.OffsetX,
            next.M21 * OffsetX + next.M22 * OffsetY + next.OffsetY, SourceSpaceId, next.TargetSpaceId);
    }
}

namespace VisionPlatform.Contracts.Geometry;

/// <summary>Pixel coordinates: x right, y down; positive angles clockwise; no display scaling.</summary>
public sealed record ImageCoordinateSpace(string Id = "image", double OriginX = 0, double OriginY = 0);
public readonly record struct Point2D(double X, double Y);
public sealed record Pose2D(double CenterX, double CenterY, double AngleDegrees = 0, double Scale = 1,
    string CoordinateSpaceId = "image");
public sealed record RectRoi(double X, double Y, double Width, double Height)
{
    public RotatedRectRoi ToRotated(string coordinateSpaceId = "image") =>
        new(X + Width / 2, Y + Height / 2, Width, Height, 0, coordinateSpaceId);
}
public sealed record RotatedRectRoi(double CenterX, double CenterY, double Width, double Height,
    double AngleDegrees = 0, string CoordinateSpaceId = "image")
{
    public Point2D[] Corners()
    {
        var radians = AngleDegrees * Math.PI / 180;
        var c = Math.Cos(radians); var s = Math.Sin(radians);
        return new[] { (-Width / 2, -Height / 2), (Width / 2, -Height / 2),
            (Width / 2, Height / 2), (-Width / 2, Height / 2) }
            .Select(p => new Point2D(CenterX + c * p.Item1 - s * p.Item2,
                CenterY + s * p.Item1 + c * p.Item2)).ToArray();
    }
    public void Validate()
    {
        if (new[] { CenterX, CenterY, Width, Height, AngleDegrees }.Any(v => !double.IsFinite(v)) ||
            Width <= 0 || Height <= 0 || string.IsNullOrWhiteSpace(CoordinateSpaceId))
            throw new ArgumentException("ROI geometry must be finite with positive width and height.");
    }
}

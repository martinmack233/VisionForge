using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Contracts.Collections;
public sealed record EllipseGeometry(Point2D Center, double Width, double Height, double AngleDegrees, string CoordinateSpaceId);
/// <summary>Individual boundary geometry; area is absolute enclosed polygon area, not pixel count or net-with-holes.</summary>
public sealed record ContourAnalysis(double? Area, double? SignedArea, double? Perimeter, RectRoi? AxisAlignedBounds,
    RotatedRectRoi? MinimumRectangle, EllipseGeometry? Ellipse, IReadOnlyList<Point2D> ConvexHull,
    IReadOnlyList<Point2D> Approximation, double? Circularity, double? AspectRatio, double? Rectangularity, string? Detail);

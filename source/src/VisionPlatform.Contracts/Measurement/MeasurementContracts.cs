using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Contracts.Measurement;

/// <summary>Original image pixel centers, x right / y down. Published collections are read-only snapshots.</summary>
public sealed record EdgePointSet(IReadOnlyList<Point2D> Points, string CoordinateSpaceId = "image");
public sealed record LineGeometry(Point2D Origin, Point2D Direction, string CoordinateSpaceId = "image");
public sealed record CircleGeometry(Point2D Center, double Radius, string CoordinateSpaceId = "image");
public enum MeasurementOverlayKind { Points, Line, Circle, Label }
/// <summary>Display data only; no WPF types, no image ownership, no change to editable ROI.</summary>
public sealed record MeasurementOverlay(MeasurementOverlayKind Kind, string Label,
    IReadOnlyList<Point2D> Points, string CoordinateSpaceId = "image", double Radius = 0);
public sealed record MeasurementSummary(string Kind, bool Valid, string Unit,
    IReadOnlyDictionary<string, double> Values, string? ErrorReason = null);
public sealed record MeasurementResult(bool Valid, string Kind, string CoordinateSpaceId, string Unit,
    IReadOnlyDictionary<string, double> Values, IReadOnlyList<MeasurementOverlay> Overlays, string? ErrorReason = null)
{
    public MeasurementSummary Summary => new(Kind, Valid, Unit, Values, ErrorReason);
}

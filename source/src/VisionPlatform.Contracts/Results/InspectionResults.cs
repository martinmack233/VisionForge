using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Contracts.Results;

/// <summary>A contiguous foreground run in the detector's input raster (local integer indices).</summary>
public readonly record struct BlobPixelRun(int X, int Y, int Length);

public sealed record BlobResult(int Label, int Area, double CenterX, double CenterY, RectRoi Bounds)
{
    // Additive measurement metadata: the original positional constructor and port type are preserved.
    public double? Perimeter { get; init; }
    public double? Circularity => Perimeter is > 0 ? 4 * Math.PI * Area / (Perimeter.Value * Perimeter.Value) : null;
    public IReadOnlyList<BlobPixelRun>? PixelRuns { get; init; }
}
public sealed record BlobResults(IReadOnlyList<BlobResult> Items, string CoordinateSpaceId = "image")
{
    public int Count => Items.Count;
    public int RasterWidth { get; init; }
    public int RasterHeight { get; init; }
    public double RasterOriginX { get; init; }
    public double RasterOriginY { get; init; }
}
public sealed record InspectionDecision(bool IsOk, string Reason);

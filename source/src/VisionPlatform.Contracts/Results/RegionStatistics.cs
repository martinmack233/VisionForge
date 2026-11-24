using VisionPlatform.Contracts.Geometry;
namespace VisionPlatform.Contracts.Results;
public sealed record RegionStatistics(bool Valid, double? Mean, double? StandardDeviation, int PixelCount, int NonZeroCount,
    double? ForegroundRatio, Guid FrameId, ImageCoordinateSpace CoordinateSpace, string? Error);

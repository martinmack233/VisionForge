using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Contracts.Recognition;

/// <summary>Original image pixel centers; x right, y down. Display-only snapshots own no image buffers.</summary>
public enum RecognitionOverlayKind { Polygon, Polyline, Points, Label }
public sealed record RecognitionOverlay(RecognitionOverlayKind Kind, string Label,
    IReadOnlyList<Point2D> Points, string CoordinateSpaceId = "image");
public sealed record OcrWord(string Text, double Confidence, RectRoi Bounds);
public enum OcrStatus { Recognized, NoText, LowConfidence, MissingLanguageModel, EngineError, ResourceLimit }
public sealed record OcrResult(bool Success, string Text, double Confidence,
    IReadOnlyList<OcrWord> Words, string CoordinateSpaceId, TimeSpan ExecutionTime,
    IReadOnlyList<RecognitionOverlay> Overlays, string? FailureReason = null)
{
    public OcrStatus Status { get; init; } = Success ? OcrStatus.Recognized : OcrStatus.NoText;
}
/// <summary>Points are decoder localization points, not a guaranteed full symbol bounding box. No invented confidence.</summary>
public sealed record CodeReading(string Text, string Format, IReadOnlyList<Point2D> Points);
public sealed record CodeReadResult(bool Success, IReadOnlyList<CodeReading> Codes,
    string CoordinateSpaceId, TimeSpan ExecutionTime, IReadOnlyList<RecognitionOverlay> Overlays,
    string? FailureReason = null)
{
    public string Text => Codes.FirstOrDefault()?.Text ?? "";
    public string Format => Codes.FirstOrDefault()?.Format ?? "";
}
public sealed record YoloDetection(int ClassId, string Label, double Confidence, RectRoi Bounds);
public sealed record YoloInferenceResult(bool Success, IReadOnlyList<YoloDetection> Detections,
    string CoordinateSpaceId, TimeSpan ExecutionTime, IReadOnlyList<RecognitionOverlay> Overlays,
    string ModelSha256 = "", string Provider = "", int TileCount = 0,
    double ForwardMilliseconds = 0, string? FailureReason = null)
{
    /// <summary>Identity of the borrowed input frame; never a display thumbnail identity.</summary>
    public Guid FrameId { get; init; }
    public Guid RunId { get; init; }
    public string TaskId { get; init; } = "";
    public string AIProjectId { get; init; } = "";
    public string AIWorkspaceId { get; init; } = "";
    public string ModelRecordId { get; init; } = "";
    public IReadOnlyDictionary<string, double> Timings { get; init; } = new Dictionary<string, double>();
}

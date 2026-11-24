namespace VisionPlatform.Contracts.Results;

/// <summary>Structured current-session inspection data, independent of UI and wire format.</summary>
public sealed record InspectionReport
{
    public Guid InspectionId { get; init; }
    public bool IsOk { get; init; }
    public int? BlobCount { get; init; }
    public double? MatchScore { get; init; }
    public double? CenterX { get; init; }
    public double? CenterY { get; init; }
    public double? Angle { get; init; }
    public double? Scale { get; init; }
    public double? ExecutionMilliseconds { get; init; }
    public CommunicationStatus? Communication { get; init; }
    public VisionPlatform.Contracts.Measurement.MeasurementSummary? Measurement { get; init; }
}

public enum CommunicationState { Disabled, Connected, Listening, Sent, Received, Closed, DuplicateSuppressed, Failed }
public sealed record CommunicationStatus(CommunicationState State, bool Success, string Description,
    int BytesTransferred = 0, string? ErrorCode = null);

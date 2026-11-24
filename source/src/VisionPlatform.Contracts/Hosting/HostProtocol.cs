using System.Text.Json;

namespace VisionPlatform.Contracts.Hosting;

/// <summary>Wire protocol v1: UTF8 JSON, unsigned 4-byte big-endian length prefix; no image pointers/pixels.</summary>
public sealed record HostRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = "";
    public string Operation { get; init; } = "";
    public JsonElement? Payload { get; init; }
}
public sealed record HostResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public string RequestId { get; init; } = "";
    public bool Success { get; init; }
    public string ErrorCode { get; init; } = "";
    public string Message { get; init; } = "";
    public JsonElement? Payload { get; init; }
}
public enum HostSessionState { Empty, Ready, Running, Completed, Failed, Cancelled, Closed }
public static class HostErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST", UnsupportedVersion = "UNSUPPORTED_VERSION",
        UnknownOperation = "UNKNOWN_OPERATION", Busy = "BUSY", NotLoaded = "NOT_LOADED",
        ProjectInvalid = "PROJECT_INVALID", RunNotFound = "RUN_NOT_FOUND", NotReady = "NOT_READY",
        ResultTooLarge = "RESULT_TOO_LARGE", Cancelled = "CANCELLED", InternalError = "INTERNAL_ERROR";
}

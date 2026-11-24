using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Hosting;

public sealed record RuntimeHostOptions
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public bool AllowLan { get; init; }
    public int MaximumClients { get; init; } = 8;
    public int ReadTimeoutMilliseconds { get; init; } = 30000;
    public int WriteTimeoutMilliseconds { get; init; } = 5000;
    public int MaximumMessageBytes { get; init; } = 1048576;
    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out var ip)) throw new ArgumentException("ListenAddress must be an IP address.");
        if (!IPAddress.IsLoopback(ip) && !AllowLan) throw new ArgumentException("Non-loopback listening requires explicit AllowLan / --allow-lan.");
        if (Port is < 0 or > 65535 || MaximumClients is < 1 or > 64) throw new ArgumentException("Invalid host port or client limit.");
        if (ReadTimeoutMilliseconds is < 1 or > 600000 || WriteTimeoutMilliseconds is < 1 or > 600000)
            throw new ArgumentException("Host read/write timeouts must be in [1,600000] ms.");
        if (MaximumMessageBytes is < 4096 or > 1048576) throw new ArgumentException("Host message budget must be in [4096,1048576] bytes.");
    }
}
public sealed record LoadProjectCommand(string ProjectPath);
public sealed record RunProjectCommand(string? TargetNodeId = null, int TimeoutMilliseconds = 60000);
public sealed record RunReference(Guid? RunId = null);
public sealed record HostRunAccepted(Guid RunId, Guid SessionId);
public sealed record HostStatusSnapshot(Guid SessionId, HostSessionState State, string? ProjectName,
    Guid? RunId, bool CancellationRequested, string? ErrorMessage);
public sealed record HostOutputSnapshot(string Type, JsonElement Value, bool Truncated = false);
public sealed record HostNodeSnapshot(string NodeId, NodeExecutionStatus Status, NodeSkipReason SkipReason,
    double ExecutionMilliseconds, string? ErrorMessage, IReadOnlyDictionary<string, HostOutputSnapshot> Outputs);
public sealed record HostResultSnapshot(Guid RunId, Guid? EngineRunId, NodeExecutionStatus Status,
    bool Success, double ExecutionMilliseconds, string? ErrorMessage, IReadOnlyList<HostNodeSnapshot> Nodes);
public sealed class HostCommandException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }
public static class HostJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonDefaults.Options)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    public static T Payload<T>(HostRequest request, T? defaultValue = default)
    {
        if (request.Payload is null) return defaultValue ?? throw new HostCommandException(HostErrorCodes.InvalidRequest, "A payload object is required.");
        return request.Payload.Value.Deserialize<T>(Options) ?? throw new HostCommandException(HostErrorCodes.InvalidRequest, "Payload is null.");
    }
    public static HostResponse Ok(HostRequest request, object? payload = null, string message = "") => new()
    { RequestId = request.RequestId, Success = true, Message = message, Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, JsonDefaults.Options) };
    public static HostResponse Error(string requestId, string code, string message) => new()
    { RequestId = requestId, Success = false, ErrorCode = code, Message = message.Length <= 4096 ? message : message[..4096] };
}

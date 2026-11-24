using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Algorithms.Results;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record TcpConnectParameters
{
    [DisplayName("连接名称")] public string ConnectionId { get; init; } = "client";
    [DisplayName("连接设置")] public TcpConnectionOptions Options { get; init; } = new();
}
public sealed record TcpServerStartParameters
{
    [DisplayName("服务名称")] public string ServerId { get; init; } = "server";
    [DisplayName("监听设置")] public TcpListenOptions Options { get; init; } = new();
}
public sealed record TcpServerAcceptParameters
{
    [DisplayName("接入连接名称")] public string ConnectionId { get; init; } = "accepted-client";
}
public sealed record TcpSendParameters
{
    [DisplayName("明确启用发送")] public bool SendEnabled { get; init; }
    [DisplayName("发送标识（同会话同节点去重）")] public string DeliveryKey { get; init; } = "";
    [DisplayName("明确允许重复发送")] public bool ForceResend { get; init; }
    [DisplayName("未接数据端口时的消息")] public string LiteralMessage { get; init; } = "";
    [DisplayName("结果格式")] public ResultFormatOptions ResultFormat { get; init; } = new();
    [DisplayName("覆盖写超时 ms"), Range(1, 600000)] public int? WriteTimeoutMilliseconds { get; init; }
}
public sealed record TcpReceiveParameters
{
    [DisplayName("覆盖读取超时 ms"), Range(1, 600000)] public int? ReadTimeoutMilliseconds { get; init; }
}
public sealed record TcpCloseParameters
{
    [DisplayName("执行关闭")] public bool Enabled { get; init; } = true;
}

internal static class TcpNodeHelpers
{
    public static IReadOnlyList<string> Id(string id) => string.IsNullOrWhiteSpace(id) || id.Length > 128 ? ["TCP resource name must have 1 to 128 nonblank characters."] : [];
    public static IReadOnlyList<string> Timeout(int? value) => value is < 1 or > 600000 ? ["Timeout must be in [1,600000] milliseconds."] : [];
    public static NodeOutput Output(CommunicationStatus status, params (string Name, object Value)[] extra)
    {
        var values = new Dictionary<string, object> { ["Status"] = status, ["Success"] = status.Success };
        foreach (var (name, value) in extra) values.Add(name, value); return new(values);
    }
    public static string Message(NodeExecutionContext context, TcpSendParameters parameters)
    {
        var hasMessage = context.TryInput<string>("Message", out var message);
        var hasResult = context.TryInput<InspectionReport>("Result", out var report);
        if (hasMessage && hasResult) throw new ArgumentException("Connect Message or Result, not both.");
        return hasMessage ? message : hasResult ? ResultFormatter.Format(report, parameters.ResultFormat) : parameters.LiteralMessage;
    }
    public static IReadOnlyList<string> SendValidation(TcpSendParameters p)
    {
        var errors = Timeout(p.WriteTimeoutMilliseconds).ToList();
        // A connected DeliveryKey may supply the required identifier at execution time.
        if (p.DeliveryKey is null || p.DeliveryKey.Length > 256) errors.Add("DeliveryKey must contain at most 256 characters.");
        if (p.LiteralMessage is null) errors.Add("LiteralMessage cannot be null.");
        errors.AddRange(ResultFormatter.Validate(p.ResultFormat));
        return errors;
    }
}

public sealed class TcpConnectNode : NodeBase<TcpConnectParameters>
{
    public override NodeDescriptor Descriptor => Describe("TcpConnect", [new("Port", typeof(int), false)],
        [new("Connection", typeof(TcpConnectionReference)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(TcpConnectParameters p) => TcpNodeHelpers.Id(p.ConnectionId).Concat(TcpRuntime.Validate(p.Options)).ToArray();
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<TcpConnectParameters>(); var options = context.TryInput<int>("Port", out var port) ? p.Options with { Port = port } : p.Options;
        var connection = await TcpRuntime.For(context.Services).ConnectAsync(p.ConnectionId, options, cancellationToken).ConfigureAwait(false);
        return TcpNodeHelpers.Output(new(CommunicationState.Connected, true, "TCP connection established or reused."), ("Connection", connection));
    }
}
public sealed class TcpSendNode : NodeBase<TcpSendParameters>
{
    public override NodeDescriptor Descriptor => TcpSendExecution.Descriptor("TcpSend");
    protected override IReadOnlyList<string> Validate(TcpSendParameters p) => TcpNodeHelpers.SendValidation(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => TcpSendExecution.Execute(context, cancellationToken);
}
public sealed class TcpServerSendNode : NodeBase<TcpSendParameters>
{
    public override NodeDescriptor Descriptor => TcpSendExecution.Descriptor("TcpServerSend");
    protected override IReadOnlyList<string> Validate(TcpSendParameters p) => TcpNodeHelpers.SendValidation(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => TcpSendExecution.Execute(context, cancellationToken);
}
internal static class TcpSendExecution
{
    public static NodeDescriptor Descriptor(string id) => new(id, "1.0.0",
        [new("Connection", typeof(TcpConnectionReference)), new("Message", typeof(string), false), new("Result", typeof(InspectionReport), false), new("DeliveryKey", typeof(string), false)],
        [new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool)), new("BytesSent", typeof(int))]);
    public static async ValueTask<NodeOutput> Execute(NodeExecutionContext context, CancellationToken ct)
    {
        var p = context.Parameters<TcpSendParameters>();
        var key = context.TryInput<string>("DeliveryKey", out var supplied) ? supplied : p.DeliveryKey;
        var message = TcpNodeHelpers.Message(context, p);
        var status = await TcpRuntime.For(context.Services).SendAsync(context.Input<TcpConnectionReference>("Connection"), context.Specification.NodeId, key,
            message, p.SendEnabled, p.ForceResend, ct, p.WriteTimeoutMilliseconds).ConfigureAwait(false);
        return TcpNodeHelpers.Output(status, ("BytesSent", status.BytesTransferred));
    }
}
public sealed class TcpReceiveNode : NodeBase<TcpReceiveParameters>
{
    public override NodeDescriptor Descriptor => TcpReceiveExecution.Descriptor("TcpReceive");
    protected override IReadOnlyList<string> Validate(TcpReceiveParameters p) => TcpNodeHelpers.Timeout(p.ReadTimeoutMilliseconds);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => TcpReceiveExecution.Execute(context, cancellationToken);
}
public sealed class TcpServerReceiveNode : NodeBase<TcpReceiveParameters>
{
    public override NodeDescriptor Descriptor => TcpReceiveExecution.Descriptor("TcpServerReceive");
    protected override IReadOnlyList<string> Validate(TcpReceiveParameters p) => TcpNodeHelpers.Timeout(p.ReadTimeoutMilliseconds);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => TcpReceiveExecution.Execute(context, cancellationToken);
}
internal static class TcpReceiveExecution
{
    public static NodeDescriptor Descriptor(string id) => new(id, "1.0.0", [new("Connection", typeof(TcpConnectionReference))],
        [new("Message", typeof(string)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    public static async ValueTask<NodeOutput> Execute(NodeExecutionContext context, CancellationToken ct)
    {
        var message = await TcpRuntime.For(context.Services).ReceiveAsync(context.Input<TcpConnectionReference>("Connection"), ct,
            context.Parameters<TcpReceiveParameters>().ReadTimeoutMilliseconds).ConfigureAwait(false);
        return TcpNodeHelpers.Output(new(CommunicationState.Received, true, "A complete framed message was received."), ("Message", message));
    }
}
public sealed class TcpDisconnectNode : NodeBase<TcpCloseParameters>
{
    public override NodeDescriptor Descriptor => Describe("TcpDisconnect", [new("Connection", typeof(TcpConnectionReference))],
        [new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var enabled = context.Parameters<TcpCloseParameters>().Enabled;
        if (enabled) TcpRuntime.For(context.Services).Disconnect(context.Input<TcpConnectionReference>("Connection"));
        return ValueTask.FromResult(TcpNodeHelpers.Output(new(enabled ? CommunicationState.Closed : CommunicationState.Disabled, enabled, enabled ? "TCP connection closed." : "Disconnect is disabled.")));
    }
}
public sealed class TcpServerStartNode : NodeBase<TcpServerStartParameters>
{
    public override NodeDescriptor Descriptor => Describe("TcpServerStart", [],
        [new("Server", typeof(TcpServerReference)), new("BoundPort", typeof(int)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(TcpServerStartParameters p) => TcpNodeHelpers.Id(p.ServerId).Concat(TcpRuntime.Validate(p.Options)).ToArray();
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var p = context.Parameters<TcpServerStartParameters>();
        var server = TcpRuntime.For(context.Services).Start(p.ServerId, p.Options);
        return ValueTask.FromResult(TcpNodeHelpers.Output(new(CommunicationState.Listening, true, "TCP listener started or reused."), ("Server", server), ("BoundPort", server.BoundPort)));
    }
}
public sealed class TcpServerAcceptNode : NodeBase<TcpServerAcceptParameters>
{
    public override NodeDescriptor Descriptor => Describe("TcpServerAccept", [new("Server", typeof(TcpServerReference))],
        [new("Connection", typeof(TcpConnectionReference)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(TcpServerAcceptParameters p) => TcpNodeHelpers.Id(p.ConnectionId);
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var client = await TcpRuntime.For(context.Services).AcceptAsync(context.Input<TcpServerReference>("Server"),
            context.Parameters<TcpServerAcceptParameters>().ConnectionId, cancellationToken).ConfigureAwait(false);
        return TcpNodeHelpers.Output(new(CommunicationState.Connected, true, "TCP client accepted."), ("Connection", client));
    }
}
public sealed class TcpServerStopNode : NodeBase<TcpCloseParameters>
{
    public override NodeDescriptor Descriptor => Describe("TcpServerStop", [new("Server", typeof(TcpServerReference))],
        [new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); var enabled = context.Parameters<TcpCloseParameters>().Enabled;
        if (enabled) TcpRuntime.For(context.Services).Stop(context.Input<TcpServerReference>("Server"));
        return ValueTask.FromResult(TcpNodeHelpers.Output(new(enabled ? CommunicationState.Closed : CommunicationState.Disabled, enabled, enabled ? "TCP listener and accepted clients closed." : "Stop is disabled.")));
    }
}

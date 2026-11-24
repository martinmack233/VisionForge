using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Algorithms.Results;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record SerialSendOptions
{
    [DisplayName("明确启用发送")] public bool SendEnabled { get; init; }
    [DisplayName("发送标识（同会话去重）")] public string DeliveryKey { get; init; } = "";
    [DisplayName("明确强制重发")] public bool ForceResend { get; init; }
    [DisplayName("固定消息（无数据输入时）")] public string Message { get; init; } = "";
    [DisplayName("写超时（ms）"), Range(1, 120000)] public int TimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("结构化结果格式")] public ResultFormatOptions FormatOptions { get; init; } = new();
}
public sealed record SerialReceiveOptions
{
    [DisplayName("读超时（ms）"), Range(1, 120000)] public int TimeoutMilliseconds { get; init; } = 2000;
}
public sealed record SerialCloseOptions
{
    [DisplayName("连接标识")] public string ConnectionId { get; init; } = "serial";
}

public sealed class SerialOpenNode : NodeBase<SerialOpenOptions>
{
    public override NodeDescriptor Descriptor => Describe("SerialOpen", [],
        [new("Port", typeof(SerialPortReference)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(SerialOpenOptions parameters) => SerialSession.ValidateOpen(parameters);
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var manager = SerialSession.For(context.Services);
        var reference = await manager.OpenAsync(context.Parameters<SerialOpenOptions>(), cancellationToken).ConfigureAwait(false);
        return new NodeOutput(new Dictionary<string, object>
        { ["Port"] = reference, ["Status"] = new CommunicationStatus(CommunicationState.Connected, true, "Serial port opened."), ["Success"] = true });
    }
}
public sealed class SerialSendNode : NodeBase<SerialSendOptions>
{
    public override NodeDescriptor Descriptor => Describe("SerialSend",
        [new("Port", typeof(SerialPortReference)), new("Message", typeof(string), false), new("Result", typeof(InspectionReport), false), new("DeliveryKey", typeof(string), false)],
        [new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(SerialSendOptions p)
    {
        var errors = ResultFormatter.Validate(p.FormatOptions).ToList();
        if (p.TimeoutMilliseconds is < 1 or > 120000) errors.Add("Serial write timeout must be in [1,120000] ms.");
        if (p.DeliveryKey is null || p.DeliveryKey.Length > 256) errors.Add("DeliveryKey may contain at most 256 characters.");
        if (p.Message is null) errors.Add("Message cannot be null.");
        return errors;
    }
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<SerialSendOptions>();
        if (!p.SendEnabled) return StatusOutput(new(CommunicationState.Disabled, true, "Serial sending is disabled; no bytes sent."));
        var hasMessage = context.TryInput<string>("Message", out var message);
        var hasResult = context.TryInput<InspectionReport>("Result", out var report);
        if (hasMessage && hasResult) throw new ArgumentException("Connect Message or Result to SerialSend, not both.");
        message = hasResult ? ResultFormatter.Format(report, p.FormatOptions) : hasMessage ? message : p.Message;
        var key = context.TryInput<string>("DeliveryKey", out var supplied) ? supplied : p.DeliveryKey;
        var status = await SerialSession.For(context.Services).SendAsync(context.Input<SerialPortReference>("Port"),
            context.Spec.NodeId, key, message, p.TimeoutMilliseconds, p.ForceResend, cancellationToken).ConfigureAwait(false);
        return StatusOutput(status);
    }
    private static NodeOutput StatusOutput(CommunicationStatus status) => new(new Dictionary<string, object> { ["Status"] = status, ["Success"] = status.Success });
}
public sealed class SerialReceiveNode : NodeBase<SerialReceiveOptions>
{
    public override NodeDescriptor Descriptor => Describe("SerialReceive", [new("Port", typeof(SerialPortReference))],
        [new("Message", typeof(string)), new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(SerialReceiveOptions p) => p.TimeoutMilliseconds is < 1 or > 120000
        ? ["Serial read timeout must be in [1,120000] ms."] : [];
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var message = await SerialSession.For(context.Services).ReceiveAsync(context.Input<SerialPortReference>("Port"),
            context.Parameters<SerialReceiveOptions>().TimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        return new NodeOutput(new Dictionary<string, object>
        { ["Message"] = message, ["Status"] = new CommunicationStatus(CommunicationState.Received, true, "Serial frame received."), ["Success"] = true });
    }
}
public sealed class SerialCloseNode : NodeBase<SerialCloseOptions>
{
    public override NodeDescriptor Descriptor => Describe("SerialClose", [new("Port", typeof(SerialPortReference), false)],
        [new("Status", typeof(CommunicationStatus)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(SerialCloseOptions p) => string.IsNullOrWhiteSpace(p.ConnectionId) || p.ConnectionId.Length > 128
        ? ["Serial ConnectionId must contain 1..128 characters."] : [];
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = SerialSession.For(context.Services);
        var reference = context.TryInput<SerialPortReference>("Port", out var supplied) ? supplied : new(manager.SessionId, context.Parameters<SerialCloseOptions>().ConnectionId);
        var status = await manager.CloseAsync(reference).ConfigureAwait(false);
        return new NodeOutput(new Dictionary<string, object> { ["Status"] = status, ["Success"] = status.Success });
    }
}

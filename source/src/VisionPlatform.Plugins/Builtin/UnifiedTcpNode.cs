using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Communication.Tcp
{
    public enum TcpRole{[Display(Name="客户端")]Client,[Display(Name="服务端")]Server}
    public enum TcpPayloadKind{[Display(Name="文本（显式编码）")]Text,[Display(Name="二进制（Bytes端口）")]Binary}
    public enum TcpCommunicationOperation
    {
        [Display(Name="建立连接 / 启动监听")]ConnectOrStart,[Display(Name="接受客户端（服务端）")]Accept,
        [Display(Name="发送")]Send,[Display(Name="接收完整消息")]Receive,[Display(Name="请求响应（独占事务）")]RequestResponse,
        [Display(Name="关闭当前连接")]Close,[Display(Name="停止监听及其接入连接")]StopListener
    }
}
namespace VisionPlatform.Plugins.Builtin
{
    public sealed record TcpCommunicationOptions
    {
        [DisplayName("角色")]public TcpRole Role{get;init;}
        [DisplayName("操作"),Description("仅明确运行时执行通信；请求响应收到完整协议帧不等于远端业务判定成功。")]public TcpCommunicationOperation Operation{get;init;}
        [DisplayName("连接名称"),AppliesWhen("Operation","ConnectOrStart","Accept")]public string ConnectionId{get;init;}="unified-client";
        [DisplayName("服务名称"),AppliesWhen("Role","Server"),AppliesWhen("Operation","ConnectOrStart")]public string ServerId{get;init;}="unified-server";
        [DisplayName("客户端设置"),AppliesWhen("Role","Client"),AppliesWhen("Operation","ConnectOrStart")]public TcpConnectionOptions Client{get;init;}=new();
        [DisplayName("监听设置"),AppliesWhen("Role","Server"),AppliesWhen("Operation","ConnectOrStart")]public TcpListenOptions Listener{get;init;}=new();
        [DisplayName("负载类型"),AppliesWhen("Operation","Send","Receive","RequestResponse")]public TcpPayloadKind Payload{get;init;}
        [DisplayName("明确启用发送"),Description("默认不发；仅Send/RequestResponse适用。编辑/加载/复制节点不会发送。"),AppliesWhen("Operation","Send","RequestResponse")]public bool SendEnabled{get;init;}
        [DisplayName("发送标识"),Description("同Runtime会话、同节点、同端点相同标识只尝试一次，包含失败或超时。"),AppliesWhen("Operation","Send","RequestResponse")]public string DeliveryKey{get;init;}="";
        [DisplayName("明确允许再次发送"),AppliesWhen("Operation","Send","RequestResponse")]public bool ForceResend{get;init;}
        [DisplayName("文本（未接Message时）"),AppliesWhen("Payload","Text"),AppliesWhen("Operation","Send","RequestResponse")]public string Message{get;init;}="";
        [DisplayName("读写超时 ms"),Range(1,600000),AppliesWhen("Operation","Send","Receive","RequestResponse")]public int TimeoutMilliseconds{get;init;}=2000;
    }
    public sealed class TcpCommunicationNode:NodeBase<TcpCommunicationOptions>,IParameterDependentInputs
    {
        public override NodeDescriptor Descriptor=>Describe("TcpCommunication",[new("Connection",typeof(TcpConnectionReference),false),new("Server",typeof(TcpServerReference),false),new("Message",typeof(string),false),new("Bytes",typeof(BinaryData),false),new("DeliveryKey",typeof(string),false),new("Port",typeof(int),false)],
            [new("Connection",typeof(TcpConnectionReference),false),new("Server",typeof(TcpServerReference),false),new("BoundPort",typeof(int),false),new("Message",typeof(string),false),new("Bytes",typeof(BinaryData),false),new("Status",typeof(CommunicationStatus)),new("Success",typeof(bool)),new("ResponseReceived",typeof(bool))]);
        public IReadOnlyList<string> RequiredInputs(JsonElement parameters)
        {var p=parameters.Deserialize<TcpCommunicationOptions>(JsonDefaults.Options)!;if(p.Operation is TcpCommunicationOperation.Accept or TcpCommunicationOperation.StopListener)return["Server"];if(p.Operation is TcpCommunicationOperation.Send or TcpCommunicationOperation.Receive or TcpCommunicationOperation.RequestResponse or TcpCommunicationOperation.Close){var ports=new List<string>{"Connection"};if(p.SendEnabled&&p.Payload==TcpPayloadKind.Binary&&p.Operation is TcpCommunicationOperation.Send or TcpCommunicationOperation.RequestResponse)ports.Add("Bytes");return ports;}return[];}
        protected override IReadOnlyList<string> Validate(TcpCommunicationOptions p)
        {
            var errors=new List<string>();if(!Enum.IsDefined(p.Role)||!Enum.IsDefined(p.Operation)||(p.Operation is TcpCommunicationOperation.Send or TcpCommunicationOperation.Receive or TcpCommunicationOperation.RequestResponse)&&!Enum.IsDefined(p.Payload))errors.Add("请选择有效TCP角色、操作和负载类型。");
            if(p.Role!=TcpRole.Server&&p.Operation is TcpCommunicationOperation.Accept or TcpCommunicationOperation.StopListener)errors.Add("接受客户端/停止监听仅服务端适用。");
            if(p.Operation==TcpCommunicationOperation.ConnectOrStart)errors.AddRange(p.Role==TcpRole.Client?TcpRuntime.Validate(p.Client):TcpRuntime.Validate(p.Listener));
            if(p.Operation is TcpCommunicationOperation.ConnectOrStart or TcpCommunicationOperation.Accept)errors.AddRange(TcpNodeHelpers.Id(p.ConnectionId));
            if(p.Role==TcpRole.Server&&p.Operation==TcpCommunicationOperation.ConnectOrStart)errors.AddRange(TcpNodeHelpers.Id(p.ServerId));
            if(p.Operation is TcpCommunicationOperation.Send or TcpCommunicationOperation.Receive or TcpCommunicationOperation.RequestResponse&&p.TimeoutMilliseconds is <1 or >600000)errors.Add("超时必须为1..600000ms。");if(p.Operation is TcpCommunicationOperation.Send or TcpCommunicationOperation.RequestResponse&&(p.DeliveryKey is null||p.DeliveryKey.Length>256||p.Payload==TcpPayloadKind.Text&&p.Message is null))errors.Add("消息/发送标识无效，标识最多256字符。");if(p.ConnectionId?.Length>110||p.ServerId?.Length>110)errors.Add("统一TCP名称最多110字符（角色命名空间由服务隔离）。");return errors;
        }
        public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var p=c.Parameters<TcpCommunicationOptions>();var runtime=TcpRuntime.For(c.Services);var extra=new Dictionary<string,object>();CommunicationStatus status;var response=false;
            if(p.Operation==TcpCommunicationOperation.ConnectOrStart)
            {
                if(p.Role==TcpRole.Client){VisionPlatform.Communication.ResourceOwnership.Claim(c.Services,"tcp/client/"+p.ConnectionId,c.Spec.NodeId);var options=c.TryInput<int>("Port",out var port)?p.Client with{Port=port}:p.Client;extra["Connection"]=await runtime.ConnectAsync("unified.client/"+p.ConnectionId,options,ct).ConfigureAwait(false);status=new(CommunicationState.Connected,true,"客户端连接已建立或复用；未发送检测结果。");}
                else{VisionPlatform.Communication.ResourceOwnership.Claim(c.Services,"tcp/listener/"+p.ServerId,c.Spec.NodeId);var options=c.TryInput<int>("Port",out var port)?p.Listener with{Port=port}:p.Listener;var server=runtime.Start("unified.server/"+p.ServerId,options);extra["Server"]=server;extra["BoundPort"]=server.BoundPort;status=new(CommunicationState.Listening,true,"服务端已监听；尚未接受客户端或发送数据。");}
            }
            else if(p.Operation==TcpCommunicationOperation.Accept){VisionPlatform.Communication.ResourceOwnership.Claim(c.Services,"tcp/accepted/"+p.ConnectionId,c.Spec.NodeId);extra["Connection"]=await runtime.AcceptAsync(c.Input<TcpServerReference>("Server"),"unified.accepted/"+p.ConnectionId,ct).ConfigureAwait(false);status=new(CommunicationState.Connected,true,"已接受服务端客户端；引用归属当前Runtime会话。");}
            else if(p.Operation==TcpCommunicationOperation.StopListener){runtime.Stop(c.Input<TcpServerReference>("Server"));status=new(CommunicationState.Closed,true,"当前监听及其所属接入连接已关闭。");}
            else
            {
                var connection=c.Input<TcpConnectionReference>("Connection");if(connection.Generation==Guid.Empty)throw new ArgumentException("新统一入口需要本次会话带代次的连接引用，请明确重新连接。");if(runtime.IsServerConnection(connection)!=(p.Role==TcpRole.Server))throw new ArgumentException("TCP引用来源与角色不符：客户端连接和服务端接入连接不可混用。");
                if(p.Operation==TcpCommunicationOperation.Close){runtime.Disconnect(connection);status=new(CommunicationState.Closed,true,"显式连接已关闭。");}
                else if(p.Operation==TcpCommunicationOperation.Receive){if(p.Payload==TcpPayloadKind.Binary)extra["Bytes"]=new BinaryData(await runtime.ReceiveBytesAsync(connection,ct,p.TimeoutMilliseconds).ConfigureAwait(false));else extra["Message"]=await runtime.ReceiveAsync(connection,ct,p.TimeoutMilliseconds).ConfigureAwait(false);response=true;status=new(CommunicationState.Received,true,"收到完整消息；远端业务含义由下游判定。");}
                else
                {
                    var key=c.TryInput<string>("DeliveryKey",out var supplied)?supplied:p.DeliveryKey;var message=p.Payload==TcpPayloadKind.Binary?(p.SendEnabled?c.Input<BinaryData>("Bytes").ToArray():[]):runtime.Encode(connection,c.TryInput<string>("Message",out var text)?text:p.Message);
                    if(p.Operation==TcpCommunicationOperation.RequestResponse){var received=await runtime.ExchangeAsync(connection,c.Spec.NodeId,key,message,p.Payload==TcpPayloadKind.Binary,p.SendEnabled,p.ForceResend,ct,p.TimeoutMilliseconds,p.TimeoutMilliseconds).ConfigureAwait(false);status=received.Status;if(received.Response is{}bytes){response=true;if(p.Payload==TcpPayloadKind.Binary)extra["Bytes"]=new BinaryData(bytes);else extra["Message"]=runtime.Decode(connection,bytes);}}
                    else if(p.Operation==TcpCommunicationOperation.Send)status=p.Payload==TcpPayloadKind.Binary?await runtime.SendBytesAsync(connection,c.Spec.NodeId,key,message,p.SendEnabled,p.ForceResend,ct,p.TimeoutMilliseconds).ConfigureAwait(false):await runtime.SendAsync(connection,c.Spec.NodeId,key,runtime.Decode(connection,message),p.SendEnabled,p.ForceResend,ct,p.TimeoutMilliseconds).ConfigureAwait(false);
                    else throw new ArgumentException("不支持的TCP操作。");
                }
            }
            if(p.Operation==TcpCommunicationOperation.RequestResponse&&!response)status=status with{Success=false};extra["Status"]=status;extra["Success"]=status.Success;extra["ResponseReceived"]=response;return new(extra);
        }
    }
}

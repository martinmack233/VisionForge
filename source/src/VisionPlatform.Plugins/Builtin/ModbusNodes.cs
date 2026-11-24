using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using VisionPlatform.Communication.Modbus;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;
public sealed record ModbusCommunicationOptions
{
    [DisplayName("传输方式")]public ModbusTransport Transport{get;init;}
    [DisplayName("操作"),Description("明确执行才通信；首版仅TCP客户端/RTU主站，非服务器产品。不自动重试或连接生产设备。") ]public ModbusOperation Operation{get;init;}
    [DisplayName("连接名称"),AppliesWhen("Operation","Connect")]public string ConnectionId{get;init;}="modbus";
    [DisplayName("TCP设置"),AppliesWhen("Transport","Tcp"),AppliesWhen("Operation","Connect")]public ModbusTcpOptions Tcp{get;init;}=new();
    [DisplayName("串口设置（非文本分帧）"),AppliesWhen("Transport","Rtu"),AppliesWhen("Operation","Connect")]public ModbusSerialOptions Serial{get;init;}=new();
    [DisplayName("事务/排队超时 ms"),Range(1,120000)]public int TimeoutMilliseconds{get;init;}=2000;
    [DisplayName("UnitId / RTU从站地址"),Range(0,255),Description("TCP UnitId为0..255；RTU只允许1..247，不广播。"),AppliesWhen("Operation","ReadCoils","ReadDiscreteInputs","ReadHoldingRegisters","ReadInputRegisters","WriteSingleCoil","WriteSingleRegister","WriteMultipleCoils","WriteMultipleRegisters")]public byte UnitId{get;init;}=1;
    [DisplayName("0基协议地址"),Range(0,65535),Description("协议从0计数。手册40001通常对应保持寄存器协议地址0，具体以设备手册为准，不自动减40001。"),AppliesWhen("Operation","ReadCoils","ReadDiscreteInputs","ReadHoldingRegisters","ReadInputRegisters","WriteSingleCoil","WriteSingleRegister","WriteMultipleCoils","WriteMultipleRegisters")]public int Address{get;init;}
    [DisplayName("读取数量"),Range(1,2000),Description("读位1..2000，读寄存器1..125；地址+数量不能超出65536。"),AppliesWhen("Operation","ReadCoils","ReadDiscreteInputs","ReadHoldingRegisters","ReadInputRegisters")]public int Quantity{get;init;}=1;
    [DisplayName("明确允许写操作（副作用）"),Description("默认关闭。超时/中断可能已写入，状态不确定；不自动重发。"),AppliesWhen("Operation","WriteSingleCoil","WriteSingleRegister","WriteMultipleCoils","WriteMultipleRegisters")]public bool WriteEnabled{get;init;}
    [DisplayName("本次写入标识"),Description("同会话、节点和端点同标识只尝试一次，包括超时；新的业务写入请明确更换标识。"),AppliesWhen("Operation","WriteSingleCoil","WriteSingleRegister","WriteMultipleCoils","WriteMultipleRegisters")]public string DeliveryKey{get;init;}="";
    [DisplayName("单线圈值"),AppliesWhen("Operation","WriteSingleCoil")]public bool CoilValue{get;init;}
    [DisplayName("单寄存器原始UInt16"),Range(0,65535),AppliesWhen("Operation","WriteSingleRegister")]public int RegisterValue{get;init;}
    [DisplayName("多线圈值"),Description("可连接强类型Coils，或在表格中填写true/false、1/0；最多1968项。输入口优先，文本仅为明确的值列表，不执行表达式。"),AppliesWhen("Operation","WriteMultipleCoils")]public string CoilValues{get;init;}="";
    [DisplayName("多寄存器原始UInt16值"),Description("可连接强类型Registers，或在表格中填写0..65535整数；最多123项。字节/字序解码使用独立节点。"),AppliesWhen("Operation","WriteMultipleRegisters")]public string RegisterValues{get;init;}="";
}
public sealed class ModbusCommunicationNode:NodeBase<ModbusCommunicationOptions>,IParameterDependentInputs
{
    public override NodeDescriptor Descriptor=>Describe("ModbusCommunication",[new("Connection",typeof(ModbusConnectionReference),false),new("Coils",typeof(ModbusBitValues),false),new("Registers",typeof(ModbusRegisterValues),false),new("DeliveryKey",typeof(string),false),new("Port",typeof(int),false)],
        [new("Connection",typeof(ModbusConnectionReference),false),new("Result",typeof(ModbusResult),false),new("Coils",typeof(ModbusBitValues),false),new("Registers",typeof(ModbusRegisterValues),false),new("Collection",typeof(VisionCollection),false),new("Status",typeof(CommunicationStatus)),new("Success",typeof(bool))]);
    public IReadOnlyList<string> RequiredInputs(JsonElement parameters)
    {var p=parameters.Deserialize<ModbusCommunicationOptions>(JsonDefaults.Options)!;var required=new List<string>();if(p.Operation!=ModbusOperation.Connect)required.Add("Connection");if(p.WriteEnabled&&p.Operation==ModbusOperation.WriteMultipleCoils&&string.IsNullOrWhiteSpace(p.CoilValues))required.Add("Coils");if(p.WriteEnabled&&p.Operation==ModbusOperation.WriteMultipleRegisters&&string.IsNullOrWhiteSpace(p.RegisterValues))required.Add("Registers");return required;}
    public static ModbusBitValues ParseCoils(string text)=>new(Split(text).Select(s=>s switch{"1"=>true,"0"=>false,_=>bool.TryParse(s,out var b)?b:throw new ArgumentException("线圈值必须为true/false或1/0。") }));
    public static ModbusRegisterValues ParseRegisters(string text)=>new(Split(text).Select(s=>ushort.TryParse(s,NumberStyles.None,CultureInfo.InvariantCulture,out var v)?v:throw new ArgumentException("寄存器值必须是0..65535整数。")));
    private static string[] Split(string text){if(text.Length>20000)throw new ArgumentException("写入值列表超过文本预算。");return text.Split([',',';',' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);}
    protected override IReadOnlyList<string> Validate(ModbusCommunicationOptions p)
    {
        try
        {
            if(!Enum.IsDefined(p.Operation)||!Enum.IsDefined(p.Transport)||p.TimeoutMilliseconds is <1 or >120000||ModbusCodec.IsWrite(p.Operation)&&(p.DeliveryKey is null||p.DeliveryKey.Length>256))return["传输、操作、超时或写入标识无效。"];
            if(p.Operation==ModbusOperation.Connect)return ModbusRuntime.Validate(new(){ConnectionId=p.ConnectionId,Transport=p.Transport,Tcp=p.Tcp,Serial=p.Serial.ToSerial(p.ConnectionId,p.TimeoutMilliseconds),TimeoutMilliseconds=p.TimeoutMilliseconds});
            if(p.Operation==ModbusOperation.Close)return[];
            if(p.Operation==ModbusOperation.WriteSingleRegister&&p.RegisterValue is <0 or >65535)return["单寄存器值必须为0..65535。"];
            if(p.Operation is ModbusOperation.WriteMultipleCoils or ModbusOperation.WriteMultipleRegisters)
            {
                if(p.Address is <0 or >65535||p.Transport==ModbusTransport.Rtu&&p.UnitId is <1 or >247)return["地址/从站地址无效。"];
                var coils=p.Operation!=ModbusOperation.WriteMultipleCoils||string.IsNullOrWhiteSpace(p.CoilValues)?null:ParseCoils(p.CoilValues);var registers=p.Operation!=ModbusOperation.WriteMultipleRegisters||string.IsNullOrWhiteSpace(p.RegisterValues)?null:ParseRegisters(p.RegisterValues);
                if(p.Operation==ModbusOperation.WriteMultipleCoils&&coils is not null)ModbusCodec.RequestPdu(new(p.Operation,p.UnitId,p.Address,Coils:coils),p.Transport);
                if(p.Operation==ModbusOperation.WriteMultipleRegisters&&registers is not null)ModbusCodec.RequestPdu(new(p.Operation,p.UnitId,p.Address,Registers:registers),p.Transport);
                return[];
            }
            ModbusCodec.RequestPdu(new(p.Operation,p.UnitId,p.Address,p.Quantity,p.CoilValue,(ushort)p.RegisterValue),p.Transport);return[];
        }
        catch(Exception ex)when(ex is ArgumentException or FormatException or OverflowException){return[ex.Message];}
    }
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var p=c.Parameters<ModbusCommunicationOptions>();var runtime=ModbusRuntime.For(c.Services);var outputs=new Dictionary<string,object>();CommunicationStatus status;
        if(p.Operation==ModbusOperation.Connect)
        {
            VisionPlatform.Communication.ResourceOwnership.Claim(c.Services,"modbus/"+p.ConnectionId,c.Spec.NodeId);
            var tcp=c.TryInput<int>("Port",out var port)?p.Tcp with{Port=port}:p.Tcp;
            outputs["Connection"]=await runtime.OpenAsync(new(){ConnectionId=p.ConnectionId,Transport=p.Transport,Tcp=tcp,Serial=p.Serial.ToSerial(p.ConnectionId,p.TimeoutMilliseconds),TimeoutMilliseconds=p.TimeoutMilliseconds},ct).ConfigureAwait(false);
            status=new(CommunicationState.Connected,true,"Modbus连接已建立/复用；尚未读写设备。");
        }
        else
        {
            var reference=c.Input<ModbusConnectionReference>("Connection");if(reference.Transport!=p.Transport)throw new ArgumentException("当前传输方式与连接引用不符；不自动切换TCP/RTU。");
            if(p.Operation==ModbusOperation.Close){await runtime.CloseAsync(reference);status=new(CommunicationState.Closed,true,"已关闭此会话的显式连接。");}
            else
            {
                var coils=p.Operation==ModbusOperation.WriteMultipleCoils?(c.TryInput<ModbusBitValues>("Coils",out var bits)?bits:ParseCoils(p.CoilValues)):null;
                var registers=p.Operation==ModbusOperation.WriteMultipleRegisters?(c.TryInput<ModbusRegisterValues>("Registers",out var words)?words:ParseRegisters(p.RegisterValues)):null;
                var request=new ModbusRequest(p.Operation,p.UnitId,p.Address,p.Quantity,p.CoilValue,p.Operation==ModbusOperation.WriteSingleRegister?checked((ushort)p.RegisterValue):(ushort)0,coils,registers);
                var key=c.TryInput<string>("DeliveryKey",out var supplied)?supplied:p.DeliveryKey;
                var result=await runtime.ExecuteAsync(reference,request,c.Spec.NodeId,key,p.WriteEnabled,ct,p.TimeoutMilliseconds).ConfigureAwait(false);outputs["Result"]=result;outputs["Coils"]=result.Coils;outputs["Registers"]=result.Registers;
                ItemOrigin Origin(int i)=>new(c.RunId,c.Spec.NodeId,i,null,"modbus-registers",$"{c.RunId:N}/{c.Spec.NodeId}/{result.RequestId:N}/{i}");
                outputs["Collection"]=new VisionCollection(VisionItemKind.Scalar,result.Coils.Count>0?result.Coils.Select((v,i)=>(VisionItem)new ScalarItem(Origin(i),v)):result.Registers.Select((v,i)=>(VisionItem)new ScalarItem(Origin(i),(double)v)));
                status=new(result.DuplicateSuppressed?CommunicationState.DuplicateSuppressed:CommunicationState.Received,!result.DuplicateSuppressed&&result.ResponseReceived,result.DuplicateSuppressed?"相同写标识已尝试，未重发也未消费旧响应。":"已验证协议响应；业务合格性由下游判定。");
            }
        }
        outputs["Status"]=status;outputs["Success"]=status.Success;return new(outputs);
    }
}
public sealed record ModbusDecodeOptions
{
    [DisplayName("明确解码类型")]public ModbusDataType DataType{get;init;}
    [DisplayName("寄存器内部字节序")]public RegisterByteOrder ByteOrder{get;init;}
    [DisplayName("32位字序"),AppliesWhen("DataType","Int32","UInt32","Float32")]public RegisterWordOrder WordOrder{get;init;}
    [DisplayName("起始寄存器索引（0基）"),Range(0,124)]public int StartIndex{get;init;}
    [DisplayName("解码值数量"),Range(1,125),Description("32位值每项占2个寄存器。只有Count=1时Number单值口有效；完整结果始终在Collection。")]public int Count{get;init;}=1;
}
[SubflowSafe]
public sealed class ModbusDecodeNode:NodeBase<ModbusDecodeOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ModbusDecode",[new("Registers",typeof(ModbusRegisterValues))],[new("Collection",typeof(VisionCollection)),new("Count",typeof(int)),new("Number",typeof(double),false)]);
    protected override IReadOnlyList<string> Validate(ModbusDecodeOptions p)=>!Enum.IsDefined(p.DataType)||!Enum.IsDefined(p.ByteOrder)||!Enum.IsDefined(p.WordOrder)||p.StartIndex is <0 or >124||p.Count is <1 or >125?["解码类型、字节/字序或范围无效。"]:[];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();var p=c.Parameters<ModbusDecodeOptions>();var values=ModbusRegisterDecoder.Decode(c.Input<ModbusRegisterValues>("Registers"),p.DataType,p.ByteOrder,p.WordOrder,p.StartIndex,p.Count);var collection=new VisionCollection(VisionItemKind.Scalar,values.Select((v,i)=>new ScalarItem(new(c.RunId,c.Spec.NodeId,i,null,"decoded-registers",$"{c.RunId:N}/{c.Spec.NodeId}/{i}"),v)));var outputs=new Dictionary<string,object>{{"Collection",collection},{"Count",values.Length}};if(values.Length==1)outputs["Number"]=values[0];return ValueTask.FromResult(new NodeOutput(outputs));}
}

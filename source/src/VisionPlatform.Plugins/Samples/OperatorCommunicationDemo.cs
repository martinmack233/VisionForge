using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCvSharp;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Communication.Modbus;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;
public sealed record SimulatedFrame(string RequestHex,string ResponseHex);
public sealed record CommunicationDemoReport(string Transport,bool Simulation,bool HardwareVerified,IReadOnlyList<SimulatedFrame> Frames,string Note);
/// <summary>Explicit, finite, loopback-only teaching harness. NOT a production Modbus server or device driver.</summary>
public static class OperatorCommunicationDemo
{
    public static readonly string[] Ids=["operators-tcp-request","operators-modbus-tcp"];
    public static async Task<CommunicationDemoReport> RunTcpAsync(Func<int,CancellationToken,Task> execute,CancellationToken ct=default,int clients=1)
    {
        if(clients is <1 or >4)throw new ArgumentException("有限本机演示仅允许1..4个顺序连接。");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(15000);var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(1);Task? peer=null;var frames=new List<SimulatedFrame>();
        try
        {
            peer=Task.Run(async()=>{for(var clientIndex=0;clientIndex<clients;clientIndex++){using var client=await listener.AcceptTcpClientAsync(deadline.Token);using var stream=client.GetStream();var protocol=new FrameProtocolOptions();var message=await new FramedMessageReader(stream,protocol).ReadAsync(deadline.Token);if(message!="PING,2")throw new InvalidDataException("本机示例请求应为PING,2。");var reply=FrameCodec.Encode("PONG,2",protocol);foreach(var part in reply.Chunk(2))await stream.WriteAsync(part,deadline.Token);frames.Add(new(Convert.ToHexString(FrameCodec.Encode(message,protocol)),Convert.ToHexString(reply)));}},deadline.Token);
            await execute(((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);await peer;return new("TCP Loopback",true,false,frames.AsReadOnly(),"实际分片发送/完整分帧接收；PONG是本机教学响应，不是生产设备业务确认。");
        }
        finally{deadline.Cancel();listener.Stop();if(peer is not null)try{await peer;}catch(Exception)when(deadline.IsCancellationRequested){}}
    }
    public static async Task<CommunicationDemoReport> RunModbusTcpAsync(Func<int,CancellationToken,Task> execute,CancellationToken ct=default,int clients=1)
    {
        if(clients is <1 or >4)throw new ArgumentException("有限本机演示仅允许1..4个顺序连接。");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(15000);var listener=new TcpListener(IPAddress.Loopback,0);listener.Start(1);Task? peer=null;var frames=new List<SimulatedFrame>();var device=new SimulatedModbusDevice();
        try
        {
            peer=Task.Run(async()=>{for(var clientIndex=0;clientIndex<clients;clientIndex++){using var client=await listener.AcceptTcpClientAsync(deadline.Token);using var stream=client.GetStream();for(var i=0;i<64;i++){var header=new byte[7];var first=await stream.ReadAsync(header.AsMemory(0,1),deadline.Token);if(first==0)break;await stream.ReadExactlyAsync(header.AsMemory(1),deadline.Token);var length=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));if(length is <2 or >254||header[2]!=0||header[3]!=0)throw new InvalidDataException("模拟器拒绝无效MBAP。");var pdu=new byte[length-1];await stream.ReadExactlyAsync(pdu,deadline.Token);var response=device.Respond(header[6],pdu);var packet=new byte[response.Length+7];header.AsSpan(0,2).CopyTo(packet);BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4),(ushort)(response.Length+1));packet[6]=header[6];response.CopyTo(packet,7);frames.Add(new(Convert.ToHexString(header.Concat(pdu).ToArray()),Convert.ToHexString(packet)));foreach(var part in packet.Chunk(2))await stream.WriteAsync(part,deadline.Token);}}},deadline.Token);
            await execute(((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);await peer;return new("Modbus TCP Loopback",true,false,frames.AsReadOnly(),"有限教学模拟器；实际MBAP事务/拆包验证，非实体Modbus设备或服务端产品。");
        }
        finally{deadline.Cancel();listener.Stop();if(peer is not null)try{await peer;}catch(Exception)when(deadline.IsCancellationRequested){}}
    }
    public static ProjectDocument Project(int index,bool simulationWrites=false)
    {
        NodeSpecification N(string id,string type,object p)=>new(id,type,type,JsonDefaults.Element(p));var nodes=new List<NodeSpecification>();var edges=new List<NodeConnection>();
        if(index==0)
        {
            nodes.Add(N("connect","TcpCommunication",new TcpCommunicationOptions{Client=new(){Port=1},ConnectionId="owned-demo"}));nodes.Add(N("exchange","TcpCommunication",new TcpCommunicationOptions{Operation=TcpCommunicationOperation.RequestResponse,Message="PING,2",SendEnabled=true,DeliveryKey="self-authored-tcp-demo"}));nodes.Add(N("text","TextDisplay",new TextDisplayParameters()));nodes.Add(N("close","TcpCommunication",new TcpCommunicationOptions{Operation=TcpCommunicationOperation.Close}));
            edges.AddRange([new("connect","Connection","exchange","Connection"),new("exchange","Message","text","String"),new("connect","Connection","close","Connection"),new("exchange",NodePorts.Completed,"close",NodePorts.Execute)]);
        }
        else
        {
            nodes.Add(N("connect","ModbusCommunication",new ModbusCommunicationOptions{Tcp=new(){Port=1},ConnectionId="owned-modbus-demo"}));nodes.Add(N("read","ModbusCommunication",new ModbusCommunicationOptions{Operation=ModbusOperation.ReadHoldingRegisters,Quantity=3}));nodes.Add(N("decode","ModbusDecode",new ModbusDecodeOptions{Count=3}));nodes.Add(N("select","CollectionSelect",new CollectionIndexOptions{Index=1}));nodes.Add(N("value","ItemValue",new ItemValueOptions()));nodes.Add(N("text","TextDisplay",new TextDisplayParameters()));
            edges.AddRange([new("connect","Connection","read","Connection"),new("read","Registers","decode","Registers"),new("decode","Collection","select","Collection"),new("select","Item","value","Item"),new("value","Number","text","Number")]);var previous="read";
            if(simulationWrites){nodes.Add(N("write","ModbusCommunication",new ModbusCommunicationOptions{Operation=ModbusOperation.WriteMultipleRegisters,Address=8,RegisterValues="1,2,65535",WriteEnabled=true,DeliveryKey="EXPLICIT-SIMULATION-WRITE"}));nodes.Add(N("readback","ModbusCommunication",new ModbusCommunicationOptions{Operation=ModbusOperation.ReadHoldingRegisters,Address=8,Quantity=3}));edges.AddRange([new("connect","Connection","write","Connection"),new("read",NodePorts.Completed,"write",NodePorts.Execute),new("connect","Connection","readback","Connection"),new("write",NodePorts.Completed,"readback",NodePorts.Execute)]);previous="readback";}
            nodes.Add(N("close","ModbusCommunication",new ModbusCommunicationOptions{Operation=ModbusOperation.Close}));edges.AddRange([new("connect","Connection","close","Connection"),new(previous,NodePorts.Completed,"close",NodePorts.Execute)]);
        }
        return new(){ProjectName=index==0?"统一TCP本机请求响应":"Modbus TCP本机只读",Nodes=nodes,Connections=edges};
    }
    public static async Task<CommunicationDemoReport> RunRtuSimulationAsync(CancellationToken ct=default)
    {
        var transport=new SimulatedRtuTransport();await using var session=new RuntimeExecutionSession();session.GetOrCreate(ModbusRuntime.ServiceKey,()=>new ModbusRuntime(session.SessionId,session.LifetimeCancellation,()=>transport));var registry=BuiltinRegistration.CreateRegistry();var nodes=new List<NodeSpecification>();var edges=new List<NodeConnection>();
        nodes.Add(new("connect","ModbusCommunication","RTU模拟连接（不打开COM）",JsonDefaults.Element(new ModbusCommunicationOptions{Transport=ModbusTransport.Rtu,Serial=new(){PortName="COM9999"}})));var previous="connect";
        var operations=new[]{ModbusOperation.ReadCoils,ModbusOperation.ReadDiscreteInputs,ModbusOperation.ReadHoldingRegisters,ModbusOperation.ReadInputRegisters,ModbusOperation.WriteSingleCoil,ModbusOperation.WriteSingleRegister,ModbusOperation.WriteMultipleCoils,ModbusOperation.WriteMultipleRegisters,ModbusOperation.ReadHoldingRegisters};
        for(var i=0;i<operations.Length;i++){var id="rtu"+i;var options=new ModbusCommunicationOptions{Transport=ModbusTransport.Rtu,Operation=operations[i],Quantity=i==8?3:4,Address=i==8?8:operations[i]==ModbusOperation.WriteMultipleRegisters?8:0,WriteEnabled=ModbusCodec.IsWrite(operations[i]),DeliveryKey="SIMULATED-RTU-"+i,CoilValue=true,RegisterValue=65535,CoilValues="true,false,true,false",RegisterValues="1,2,65535"};nodes.Add(new(id,"ModbusCommunication",operations[i].ToString(),JsonDefaults.Element(options)));edges.Add(new("connect","Connection",id,"Connection"));edges.Add(new(previous,NodePorts.Completed,id,NodePorts.Execute));previous=id;}
        nodes.Add(new("close","ModbusCommunication","关闭模拟传输",JsonDefaults.Element(new ModbusCommunicationOptions{Transport=ModbusTransport.Rtu,Operation=ModbusOperation.Close})));edges.Add(new("connect","Connection","close","Connection"));edges.Add(new(previous,NodePorts.Completed,"close",NodePorts.Execute));
        using var run=await new RuntimeExecutor(registry,null,session).ExecuteAsync(new(nodes,edges),cancellationToken:ct);if(!run.Success)throw new InvalidOperationException(string.Join(";",run.Nodes.Values.Where(n=>n.Status!=NodeExecutionStatus.Success).Select(n=>n.NodeId+":"+n.ErrorMessage)));var result=run.Nodes["rtu8"].Output<ModbusResult>("Result");if(!result.Registers.SequenceEqual(new ushort[]{1,2,65535}))throw new InvalidDataException("RTU模拟写后读回不一致。");return new("RTU byte transport simulation",true,false,transport.Frames.AsReadOnly(),"同Runtime顺序执行8种功能码，1字节拆分与CRC核对，含明确模拟写入；从未打开COM口，物理时序/设备未验。");
    }
    public static async Task SaveAllAsync(string root,CancellationToken ct=default)
    {
        var store=new ProjectStore(BuiltinRegistration.CreateRegistry());for(var i=0;i<2;i++){var directory=Path.Combine(Path.GetFullPath(root),Ids[i]);if(Directory.Exists(directory))throw new IOException("通信示例已存在，拒绝覆盖。");Directory.CreateDirectory(directory);var project=Project(i);await store.SaveAsync(Path.Combine(directory,"project.json"),project,ct);using(var preview=new Mat(180,320,MatType.CV_8UC3,new Scalar(255,245,235))){Cv2.PutText(preview,i==0?"TCP Loopback":"Modbus: READ ONLY",new(12,90),HersheyFonts.HersheySimplex,.65,new Scalar(180,90,30),2);Cv2.ImWrite(Path.Combine(directory,"preview.png"),preview);}var instructions="先复制到可写目录，点击‘启动本地通信测试’：仅动态Loopback教学端口。普通F5的默认端口1不会自动寻找设备。示例只读，测试工具另有明确模拟写入验证。RTU通过‘RTU协议模拟’按钮或Console operator-rtu-sim验证，不打开COM、不等于硬件验收。";await File.WriteAllTextAsync(Path.Combine(directory,"README.md"),instructions,ct);var manifest=new ExampleManifest{Id=Ids[i],Name=project.ProjectName,Category="通信与数据",Description=instructions,Instructions=instructions,RequiresExplicitCommunication=true,CommunicationExpected=i==0?[new("exchange","Message",JsonDefaults.Element("PONG,2"))]:[new("value","Number",JsonDefaults.Element(200d))]};foreach(var file in Directory.EnumerateFiles(directory))manifest.Files[Path.GetFileName(file)]=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file,ct)));await File.WriteAllTextAsync(Path.Combine(directory,"manifest.json"),JsonSerializer.Serialize(manifest,JsonDefaults.Options),ct);}
    }
}
/// <summary>Finite self-authored test register map. No network listener or host/device access.</summary>
public sealed class SimulatedModbusDevice
{
    private readonly bool[] _coils=Enumerable.Range(0,65536).Select(i=>i%2==0).ToArray();private readonly ushort[] _registers=Enumerable.Range(0,65536).Select(i=>(ushort)(100*(i%100+1))).ToArray();
    public byte[] Respond(byte unit,byte[] pdu)
    {
        if(pdu.Length<5)return[(byte)(pdu[0]|0x80),3];var fc=pdu[0];if(unit!=1)return[(byte)(fc|0x80),11];var address=BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));var value=BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));if(fc is not(1 or 2 or 3 or 4 or 5 or 6 or 15 or 16))return[(byte)(fc|0x80),1];var quantity=fc is 5 or 6?1:value;
        if(quantity<1||(long)address+quantity>65536)return[(byte)(fc|0x80),2];var max=fc is 1 or 2?2000:fc is 3 or 4?125:fc==15?1968:fc==16?123:1;if(quantity>max)return[(byte)(fc|0x80),3];
        if(fc is 1 or 2){var reply=new byte[2+(quantity+7)/8];reply[0]=fc;reply[1]=(byte)(reply.Length-2);for(var i=0;i<quantity;i++)if(fc==1?_coils[address+i]:(address+i)%3==0)reply[2+i/8]|=(byte)(1<<(i%8));return reply;}
        if(fc is 3 or 4){var reply=new byte[2+quantity*2];reply[0]=fc;reply[1]=(byte)(quantity*2);for(var i=0;i<quantity;i++)BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2+2*i),fc==3?_registers[address+i]:(ushort)(address+i));return reply;}
        if(fc==5){if(value is not(0 or 0xff00))return[0x85,3];_coils[address]=value!=0;}else if(fc==6)_registers[address]=value;else{var count=fc==15?(quantity+7)/8:quantity*2;if(pdu.Length!=6+count||pdu[5]!=count)return[(byte)(fc|0x80),3];for(var i=0;i<quantity;i++){if(fc==15)_coils[address+i]=(pdu[6+i/8]&(1<<(i%8)))!=0;else _registers[address+i]=BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6+i*2));}}
        return pdu[..5];
    }
}
public sealed class SimulatedRtuTransport:ISerialTransport
{
    private readonly SimulatedModbusDevice _device=new();private readonly Channel<byte> _incoming=Channel.CreateBounded<byte>(512);private bool _open;private readonly Stream _stream;
    public List<SimulatedFrame> Frames{get;}=[];public bool IsOpen=>_open;public Stream Stream=>_stream;
    public SimulatedRtuTransport()=>_stream=new SimulatedStream(this);
    public ValueTask OpenAsync(SerialOpenOptions options,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();_open=true;return ValueTask.CompletedTask;}
    public ValueTask AbortAsync(){_open=false;_incoming.Writer.TryComplete();return ValueTask.CompletedTask;}
    public ValueTask DisposeAsync()=>AbortAsync();
    private sealed class SimulatedStream(SimulatedRtuTransport owner):Stream
    {
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){if(buffer.Length==0)return 0;try{var value=await owner._incoming.Reader.ReadAsync(ct);buffer.Span[0]=value;return 1;}catch(ChannelClosedException){return 0;}}
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){ct.ThrowIfCancellationRequested();if(!owner.IsOpen)throw new IOException("模拟RTU已关闭。");if(owner.Frames.Count>=64)throw new IOException("有限RTU模拟请求上限64。");var frame=buffer.ToArray();var request=ModbusCodec.CheckRtu(frame[0],frame[1],frame);var reply=ModbusCodec.RtuFrame(frame[0],owner._device.Respond(frame[0],request));owner.Frames.Add(new(Convert.ToHexString(frame),Convert.ToHexString(reply)));foreach(var b in reply)if(!owner._incoming.Writer.TryWrite(b))throw new IOException("模拟RTU队列超限。");return ValueTask.CompletedTask;}
        public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>WriteAsync(b.AsMemory(o,c)).AsTask().GetAwaiter().GetResult();public override void Flush(){}public override Task FlushAsync(CancellationToken ct)=>Task.CompletedTask;public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
}

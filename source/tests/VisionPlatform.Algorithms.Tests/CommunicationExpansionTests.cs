using System.Text.Json;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Communication.Modbus;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class CommunicationExpansionTests
{
    [Fact]public async Task AllEightModbusFunctionsUseRealTcpFramesAndStrongValues()
    {
        var report=await OperatorCommunicationDemo.RunModbusTcpAsync(async(port,ct)=>
        {
            await using var runtime=new ModbusRuntime(Guid.NewGuid());var connection=await runtime.OpenAsync(new(){Tcp=new(){Port=port}},ct);
            foreach(var op in new[]{ModbusOperation.ReadCoils,ModbusOperation.ReadDiscreteInputs,ModbusOperation.ReadHoldingRegisters,ModbusOperation.ReadInputRegisters,ModbusOperation.WriteSingleCoil,ModbusOperation.WriteSingleRegister,ModbusOperation.WriteMultipleCoils,ModbusOperation.WriteMultipleRegisters})
            {
                var request=new ModbusRequest(op,1,0,9,true,123,new([true,false,true]),new([42,65535]));if(op is ModbusOperation.ReadHoldingRegisters or ModbusOperation.ReadInputRegisters)request=request with{Quantity=3};
                var result=await runtime.ExecuteAsync(connection,request,op.ToString(),"EXPLICIT-SIMULATION-"+op,true,ct);Assert.True(result.ResponseReceived);Assert.NotNull(result.TransactionId);
                if(op==ModbusOperation.ReadCoils){Assert.Equal(9,result.Coils.Count);Assert.True(result.Coils[0]);Assert.False(result.Coils[1]);}
                if(op==ModbusOperation.ReadHoldingRegisters)Assert.Equal(new ushort[]{100,200,300},result.Registers.ToArray());
            }
            var before=await runtime.ExecuteAsync(connection,new(ModbusOperation.WriteSingleRegister,1,10,RegisterValue:9),"write","SAME-KEY",true,ct);var duplicate=await runtime.ExecuteAsync(connection,new(ModbusOperation.WriteSingleRegister,1,10,RegisterValue:10),"write","SAME-KEY",true,ct);Assert.True(before.ResponseReceived);Assert.True(duplicate.DuplicateSuppressed);Assert.False(duplicate.ResponseReceived);
            var back=await runtime.ExecuteAsync(connection,new(ModbusOperation.ReadHoldingRegisters,1,0,2),"readback",ct:ct);Assert.Equal(new ushort[]{42,65535},back.Registers.ToArray());await runtime.CloseAsync(connection);Assert.Equal(0,runtime.OpenConnectionCount);
        });Assert.Equal(10,report.Frames.Count);Assert.True(report.Simulation);Assert.False(report.HardwareVerified);
    }
    [Fact]public async Task RtuProtocolUsesActualRuntimeButNoComHardware()
    {var report=await OperatorCommunicationDemo.RunRtuSimulationAsync();Assert.Equal(9,report.Frames.Count);Assert.False(report.HardwareVerified);Assert.All(report.Frames,f=>Assert.NotEmpty(f.ResponseHex));Assert.Equal((ushort)0xcdc5,ModbusCodec.Crc16(Convert.FromHexString("01030000000A")));var valid=ModbusCodec.RtuFrame(1,[3,2,0,100]);valid[^1]^=1;Assert.Throws<ModbusProtocolException>(()=>ModbusCodec.CheckRtu(1,3,valid));}
    [Theory][InlineData(ModbusOperation.ReadCoils,2001)][InlineData(ModbusOperation.ReadHoldingRegisters,126)][InlineData(ModbusOperation.ReadInputRegisters,0)]
    public void ProtocolRangeAndAddressAreChecked(ModbusOperation op,int quantity)
    {Assert.Throws<ArgumentException>(()=>ModbusCodec.RequestPdu(new(op,1,0,quantity),ModbusTransport.Tcp));Assert.Throws<ArgumentException>(()=>ModbusCodec.RequestPdu(new(ModbusOperation.ReadHoldingRegisters,1,65535,2),ModbusTransport.Tcp));Assert.Throws<ArgumentException>(()=>ModbusCodec.RequestPdu(new(ModbusOperation.ReadCoils,0,0),ModbusTransport.Rtu));}
    [Theory][InlineData(0x3f80,0,RegisterByteOrder.BigEndian,RegisterWordOrder.HighWordFirst)][InlineData(0x803f,0,RegisterByteOrder.LittleEndian,RegisterWordOrder.HighWordFirst)][InlineData(0,0x3f80,RegisterByteOrder.BigEndian,RegisterWordOrder.LowWordFirst)][InlineData(0,0x803f,RegisterByteOrder.LittleEndian,RegisterWordOrder.LowWordFirst)]
    public void ExplicitRegisterByteAndWordOrders(int a,int b,RegisterByteOrder bytes,RegisterWordOrder words)
    {Assert.Equal(1d,Assert.Single(ModbusRegisterDecoder.Decode(new([(ushort)a,(ushort)b]),ModbusDataType.Float32,bytes,words,0,1)));Assert.Equal(-1d,Assert.Single(ModbusRegisterDecoder.Decode(new([65535]),ModbusDataType.Int16,bytes==RegisterByteOrder.BigEndian?bytes:RegisterByteOrder.BigEndian,words,0,1)));}
    [Fact]public void NonFiniteAndIncompleteRegisterDecodingDoesNotInventValues()
    {Assert.Throws<ArgumentException>(()=>ModbusRegisterDecoder.Decode(new([0x7f80,0]),ModbusDataType.Float32,RegisterByteOrder.BigEndian,RegisterWordOrder.HighWordFirst,0,1));Assert.Throws<ArgumentException>(()=>ModbusRegisterDecoder.Decode(new([1]),ModbusDataType.UInt32,RegisterByteOrder.BigEndian,RegisterWordOrder.HighWordFirst,0,1));}
    [Theory][InlineData("transaction")][InlineData("length")][InlineData("unit")][InlineData("disconnect")]
    public async Task InvalidTcpResponsesCloseAndInvalidateTheReference(string mode)
    {
        await RawPeer(async stream=>{var request=await Request(stream);var response=Reply(request,[3,2,0,100]);if(mode=="transaction")response[1]^=1;if(mode=="unit")response[6]=2;if(mode=="length"){response[4]=1;response[5]=255;}if(mode=="disconnect")return;
            var sent=0;
            try { foreach(var b in response){await stream.WriteAsync(new[]{b});sent++;} }
            // A bad MBAP header is rejected after its seven bytes, so closing the client may
            // reset the peer while it sends the irrelevant PDU tail. Keep fragmentation and
            // all client invalidation assertions; only tolerate this expected peer-side close.
            catch(IOException) when(sent>=7 && mode is "transaction" or "unit" or "length") { }
        },async port=>
        {await using var runtime=new ModbusRuntime(Guid.NewGuid());var connection=await runtime.OpenAsync(new(){Tcp=new(){Port=port},TimeoutMilliseconds=1000});await Assert.ThrowsAnyAsync<IOException>(()=>runtime.ExecuteAsync(connection,new(ModbusOperation.ReadHoldingRegisters,1,0),"read"));Assert.Equal(0,runtime.OpenConnectionCount);await Assert.ThrowsAsync<InvalidOperationException>(()=>runtime.ExecuteAsync(connection,new(ModbusOperation.ReadHoldingRegisters,1,0),"old"));});
    }
    [Fact]public async Task ProtocolExceptionIsDistinctAndValidExceptionFrameKeepsStreamSynchronized()
    {
        await RawPeer(async stream=>{var request=await Request(stream);await stream.WriteAsync(Reply(request,[0x83,2]));var next=await Request(stream);await stream.WriteAsync(Reply(next,[3,2,0,7]));},async port=>
        {await using var runtime=new ModbusRuntime(Guid.NewGuid());var c=await runtime.OpenAsync(new(){Tcp=new(){Port=port}});var error=await Assert.ThrowsAsync<ModbusProtocolException>(()=>runtime.ExecuteAsync(c,new(ModbusOperation.ReadHoldingRegisters,1,0),"exception"));Assert.Equal((byte)2,error.ExceptionCode);Assert.Equal(1,runtime.OpenConnectionCount);var next=await runtime.ExecuteAsync(c,new(ModbusOperation.ReadHoldingRegisters,1,0),"next");Assert.Equal((ushort)7,Assert.Single(next.Registers));});
    }
    [Fact]public async Task WriteTimeoutIsUncertainAndIsNeverAutomaticallyRetried()
    {
        var count=0;await RawPeer(async stream=>{await Request(stream);Interlocked.Increment(ref count);await Task.Delay(150);},async port=>
        {await using var runtime=new ModbusRuntime(Guid.NewGuid());var c=await runtime.OpenAsync(new(){Tcp=new(){Port=port},TimeoutMilliseconds=50});await Assert.ThrowsAsync<ModbusWriteUncertainException>(()=>runtime.ExecuteAsync(c,new(ModbusOperation.WriteSingleRegister,1,0,RegisterValue:99),"write","UNCERTAIN-SIM-WRITE",true));Assert.Equal(0,runtime.OpenConnectionCount);Assert.Contains(runtime.Events,e=>e.State=="WriteUncertain");});Assert.Equal(1,count);
    }
    [Fact]public async Task CancelledReadCannotConsumeALateResponseInAnotherTransaction()
    {
        await RawPeer(async stream=>{await Request(stream);await Task.Delay(120);},async port=>{await using var runtime=new ModbusRuntime(Guid.NewGuid());var c=await runtime.OpenAsync(new(){Tcp=new(){Port=port}});using var cancel=new CancellationTokenSource(30);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runtime.ExecuteAsync(c,new(ModbusOperation.ReadHoldingRegisters,1,0),"read",ct:cancel.Token));Assert.Equal(0,runtime.OpenConnectionCount);});
    }
    [Fact]public async Task TcpBinaryFramingPreservesNullBytesAndSerializesRequestResponse()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var protocol=new FrameProtocolOptions{Mode=MessageBoundaryMode.LengthPrefix};var received=0;using var limit=new CancellationTokenSource(5000);
        var peer=Task.Run(async()=>{using var client=await listener.AcceptTcpClientAsync(limit.Token);var stream=client.GetStream();var reader=new FramedMessageReader(stream,protocol);for(var i=0;i<2;i++){var bytes=await reader.ReadBytesAsync(limit.Token);Assert.Equal(new byte[]{0,10,255,0},bytes);Interlocked.Increment(ref received);var packet=FrameCodec.EncodeBytes(bytes,protocol);foreach(var b in packet)await stream.WriteAsync(new[]{b},limit.Token);} });
        try{using var runtime=new TcpRuntime(Guid.NewGuid());var reference=await runtime.ConnectAsync("binary",new(){Port=((IPEndPoint)listener.LocalEndpoint).Port,Protocol=protocol},limit.Token);var requests=Enumerable.Range(0,2).Select(i=>runtime.ExchangeAsync(reference,"binary","K"+i,[0,10,255,0],true,true,ct:limit.Token)).ToArray();var results=await Task.WhenAll(requests);Assert.All(results,r=>Assert.Equal(new byte[]{0,10,255,0},r.Response));var duplicate=await runtime.ExchangeAsync(reference,"binary","K0",[0],true,true,ct:limit.Token);Assert.Null(duplicate.Response);Assert.Equal(2,received);runtime.Disconnect(reference);await peer;}
        finally{limit.Cancel();listener.Stop();try{await peer;}catch(OperationCanceledException){}}
    }
    [Fact]public async Task UnifiedTcpRuntimeExampleAndNodeUseActualResponse()
    {var report=await OperatorCommunicationDemo.RunTcpAsync(async(port,ct)=>{var project=OperatorCommunicationDemo.Project(0);var connect=project.Nodes.Single(n=>n.NodeId=="connect");var p=connect.Parameters.Deserialize<TcpCommunicationOptions>(JsonDefaults.Options)!;project.Nodes[project.Nodes.IndexOf(connect)]=connect with{Parameters=JsonDefaults.Element(p with{Client=p.Client with{Port=port}})};using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(project.ToGraph(),cancellationToken:ct);Assert.True(run.Success,run.ErrorMessage);Assert.Equal("PONG,2",run.Nodes["exchange"].Output<string>("Message"));Assert.True(run.Nodes["exchange"].Output<bool>("ResponseReceived"));});Assert.Single(report.Frames);}
    [Fact]public async Task UnifiedServerAndClientRolesExecuteACompleteControlledGraphAndCloseResources()
    {
        NodeSpecification N(string id,TcpRole role,TcpCommunicationOperation op,string message="")=>new(id,"TcpCommunication",id,JsonDefaults.Element(new TcpCommunicationOptions{Role=role,Operation=op,Message=message,SendEnabled=op==TcpCommunicationOperation.Send,DeliveryKey=id,Listener=new(){Port=0},Client=new(){Port=1}}));
        var nodes=new[]{N("listen",TcpRole.Server,TcpCommunicationOperation.ConnectOrStart),N("connect",TcpRole.Client,TcpCommunicationOperation.ConnectOrStart),N("accept",TcpRole.Server,TcpCommunicationOperation.Accept),N("send",TcpRole.Client,TcpCommunicationOperation.Send,"PING"),N("receive",TcpRole.Server,TcpCommunicationOperation.Receive),N("reply",TcpRole.Server,TcpCommunicationOperation.Send,"PONG"),N("answer",TcpRole.Client,TcpCommunicationOperation.Receive),N("stop",TcpRole.Server,TcpCommunicationOperation.StopListener),N("close",TcpRole.Client,TcpCommunicationOperation.Close)};
        var edges=new List<NodeConnection>{new("listen","BoundPort","connect","Port"),new("listen","Server","accept","Server"),new("listen","Server","stop","Server")};
        foreach(var id in new[]{"send","answer","close"})edges.Add(new("connect","Connection",id,"Connection"));foreach(var id in new[]{"receive","reply"})edges.Add(new("accept","Connection",id,"Connection"));
        var chain=new[]{"connect","accept","send","receive","reply","answer","stop","close"};for(var i=1;i<chain.Length;i++)edges.Add(new(chain[i-1],NodePorts.Completed,chain[i],NodePorts.Execute));
        await using var services=new RuntimeExecutionSession();using var timeout=new CancellationTokenSource(5000);using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry(),null,services).ExecuteAsync(new(nodes,edges),cancellationToken:timeout.Token);Assert.True(run.Success,run.ErrorMessage);Assert.Equal("PING",run.Nodes["receive"].Output<string>("Message"));Assert.Equal("PONG",run.Nodes["answer"].Output<string>("Message"));var runtime=TcpRuntime.For(services);Assert.Equal(0,runtime.OpenConnectionCount);Assert.Equal(0,runtime.ListeningCount);
    }
    [Fact]public async Task OldTcpEpochCannotCloseAReopenedSameNamedConnection()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();try{using var runtime=new TcpRuntime(Guid.NewGuid());var options=new TcpConnectionOptions{Port=((IPEndPoint)listener.LocalEndpoint).Port};var old=await runtime.ConnectAsync("epoch",options);using var first=await listener.AcceptTcpClientAsync();runtime.Disconnect(old);var current=await runtime.ConnectAsync("epoch",options);using var second=await listener.AcceptTcpClientAsync();Assert.NotEqual(old.Generation,current.Generation);Assert.Throws<InvalidOperationException>(()=>runtime.Disconnect(old));Assert.Equal(1,runtime.OpenConnectionCount);runtime.Disconnect(current);}finally{listener.Stop();}
    }
    [Fact]public async Task DifferentUnifiedCreatorsCannotClaimTheSameNamedResource()
    {await using var session=new RuntimeExecutionSession();VisionPlatform.Communication.ResourceOwnership.Claim(session,"tcp/client/x","nodeA");VisionPlatform.Communication.ResourceOwnership.Claim(session,"tcp/client/x","nodeA");Assert.Throws<InvalidOperationException>(()=>VisionPlatform.Communication.ResourceOwnership.Claim(session,"tcp/client/x","nodeB"));}
    [Fact]public void ModbusIgnoresInapplicableHiddenWriteDraftFieldsButValidatesTheSelectedOperation()
    {var node=new ModbusCommunicationNode();var p=new ModbusCommunicationOptions{Operation=ModbusOperation.WriteMultipleRegisters,RegisterValues="1,65535",CoilValues="old invalid unused draft"};Assert.Empty(node.ValidateParameters(JsonDefaults.Element(p)));Assert.NotEmpty(node.ValidateParameters(JsonDefaults.Element(p with{RegisterValues="65536"})));Assert.Empty(node.ValidateParameters(JsonDefaults.Element(p with{Operation=ModbusOperation.WriteMultipleCoils,CoilValues="true,false",RegisterValues="invalid unused registers"})));}
    private static async Task RawPeer(Func<NetworkStream,Task> serve,Func<int,Task> execute)
    {var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Task? task=null;try{task=Task.Run(async()=>{using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();await serve(stream);});await execute(((IPEndPoint)listener.LocalEndpoint).Port);await task.WaitAsync(TimeSpan.FromSeconds(5));}finally{listener.Stop();if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception){}}}
    private static async Task<byte[]> Request(Stream stream){var header=new byte[7];await stream.ReadExactlyAsync(header);var pdu=new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4))-1];await stream.ReadExactlyAsync(pdu);return header.Concat(pdu).ToArray();}
    private static byte[] Reply(byte[] request,byte[] pdu){var response=new byte[pdu.Length+7];request.AsSpan(0,2).CopyTo(response);BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4),(ushort)(pdu.Length+1));response[6]=request[6];pdu.CopyTo(response,7);return response;}
}

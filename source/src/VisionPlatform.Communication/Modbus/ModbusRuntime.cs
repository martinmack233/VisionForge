using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Communication.Modbus;
/// <summary>Session-owned TCP client / RTU master. No listener, background retry or implicit connection.</summary>
public sealed class ModbusRuntime:IAsyncDisposable
{
    public const string ServiceKey="vision.modbus.v1";
    private sealed class Entry(ModbusConnectionOptions options,ModbusConnectionReference reference)
    {
        public ModbusConnectionOptions Options{get;}=options;public ModbusConnectionReference Reference{get;}=reference;
        public SemaphoreSlim Gate{get;}=new(1);public TcpClient? Tcp;public ISerialTransport? Serial;public Stream Stream=>Tcp?.GetStream()??Serial?.Stream??throw new IOException("Modbus连接未打开。");public ushort Transaction;public long LastCompleted;
        public int Closed;public bool IsOpen=>Closed==0&&(Tcp is not null||Serial?.IsOpen==true);
        public async ValueTask Close(){if(Interlocked.Exchange(ref Closed,1)!=0)return;Tcp?.Dispose();if(Serial is not null)await Serial.DisposeAsync().ConfigureAwait(false);}
    }
    private readonly Guid _session;private readonly CancellationTokenSource _lifetime;private readonly Func<ISerialTransport> _serialFactory;
    private readonly SemaphoreSlim _resources=new(1);private readonly ConcurrentDictionary<string,Entry> _entries=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Node,string Endpoint,string Key),byte> _writes=new();
    private readonly object _writeGate=new();private readonly ConcurrentQueue<ModbusEvent> _events=new();public IReadOnlyList<ModbusEvent> Events=>_events.ToArray();
    private void Log(string id,ModbusOperation operation,string state,double ms,byte? code=null){_events.Enqueue(new(DateTimeOffset.UtcNow,id,operation,state,ms,code));while(_events.Count>256)_events.TryDequeue(out _); }private int _disposed;
    public int OpenConnectionCount=>_entries.Values.Count(e=>e.IsOpen);
    public ModbusRuntime(Guid sessionId,CancellationToken lifetime=default,Func<ISerialTransport>? serialFactory=null)
    {_session=sessionId;_lifetime=CancellationTokenSource.CreateLinkedTokenSource(lifetime);_serialFactory=serialFactory??(()=>new SerialPortTransport());}
    public static ModbusRuntime For(IExecutionServices? services)=>services is null?throw new InvalidOperationException("Modbus需要Runtime会话。"):services.GetOrCreate(ServiceKey,()=>new ModbusRuntime(services.SessionId,services.LifetimeCancellation));
    public static IReadOnlyList<string> Validate(ModbusConnectionOptions p)
    {
        var errors=new List<string>();if(string.IsNullOrWhiteSpace(p.ConnectionId)||p.ConnectionId.Length>128||!Enum.IsDefined(p.Transport)||p.TimeoutMilliseconds is <1 or >120000)errors.Add("连接名称、传输方式或超时无效；超时1..120000ms。");
        if(p.Transport==ModbusTransport.Tcp){if(!IPAddress.TryParse(p.Tcp.Host,out _)||p.Tcp.Port is <1 or >65535)errors.Add("Modbus TCP需要明确IP及1..65535端口，不搜索设备。");}
        else{errors.AddRange(SerialSession.ValidateOpen(p.Serial));if(p.Serial.DataBits!=8)errors.Add("Modbus RTU必须使用8数据位。");}
        return errors;
    }
    private void Alive(){ObjectDisposedException.ThrowIf(_disposed!=0,this);_lifetime.Token.ThrowIfCancellationRequested();}
    private CancellationTokenSource Deadline(CancellationToken ct,int timeout){var source=CancellationTokenSource.CreateLinkedTokenSource(ct,_lifetime.Token);source.CancelAfter(timeout);return source;}
    public async Task<ModbusConnectionReference> OpenAsync(ModbusConnectionOptions options,CancellationToken ct=default)
    {
        Alive();var errors=Validate(options);if(errors.Count>0)throw new ArgumentException(string.Join(";",errors));using var deadline=Deadline(ct,options.TimeoutMilliseconds);var entered=false;Entry? created=null;
        try
        {
            await _resources.WaitAsync(deadline.Token).ConfigureAwait(false);entered=true;
            if(_entries.TryGetValue(options.ConnectionId,out var old)){if(old.IsOpen){if(old.Options!=options)throw new InvalidOperationException("同名Modbus配置已改变，请先关闭旧引用。");return old.Reference;}_entries.TryRemove(options.ConnectionId,out _);await old.Close();}
            if(_entries.Count>=32)throw new InvalidOperationException("Modbus会话连接上限32；请显式关闭，不无限积累。");
            created=new(options,new(_session,options.ConnectionId,options.Transport));
            if(options.Transport==ModbusTransport.Tcp){created.Tcp=new TcpClient(IPAddress.Parse(options.Tcp.Host).AddressFamily);await created.Tcp.ConnectAsync(IPAddress.Parse(options.Tcp.Host),options.Tcp.Port,deadline.Token).ConfigureAwait(false);}
            else{created.Serial=_serialFactory();await using var abort=new SerialCancellation(created.Serial,deadline.Token);await created.Serial.OpenAsync(options.Serial,deadline.Token).ConfigureAwait(false);}
            deadline.Token.ThrowIfCancellationRequested();_entries[options.ConnectionId]=created;return created.Reference;
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested&&!_lifetime.IsCancellationRequested){if(created is not null)await created.Close();throw new TimeoutException("Modbus建立连接超时；未重试。");}
        catch{if(created is not null)await created.Close();throw;}
        finally{if(entered)_resources.Release();}
    }
    private Entry Find(ModbusConnectionReference reference)
    {Alive();if(reference.SessionId!=_session||!_entries.TryGetValue(reference.Id,out var entry)||entry.Reference!=reference||!entry.IsOpen)throw new InvalidOperationException("Modbus引用过期、属于其他会话或已关闭；须明确重新连接，不复用旧响应。");return entry;}
    public async Task CloseAsync(ModbusConnectionReference reference)
    {var entry=Find(reference);await entry.Close();((ICollection<KeyValuePair<string,Entry>>)_entries).Remove(new(reference.Id,entry));}
    public async Task<ModbusResult> ExecuteAsync(ModbusConnectionReference reference,ModbusRequest request,string nodeId,string deliveryKey="",bool writeEnabled=false,CancellationToken ct=default,int? timeoutMilliseconds=null)
    {
        var entry=Find(reference);var write=ModbusCodec.IsWrite(request.Operation);
        if(write&&!writeEnabled)throw new InvalidOperationException("Modbus写操作未明确启用；没有发送数据。");var pdu=ModbusCodec.RequestPdu(request,reference.Transport);if(timeoutMilliseconds is <1 or >120000)throw new ArgumentException("Modbus超时1..120000ms。");
        using var deadline=Deadline(ct,timeoutMilliseconds??entry.Options.TimeoutMilliseconds);var acquired=false;var attempted=false;var clock=Stopwatch.StartNew();
        try
        {
            await entry.Gate.WaitAsync(deadline.Token).ConfigureAwait(false);acquired=true;Find(reference);
            if(write)
            {
                if(string.IsNullOrWhiteSpace(deliveryKey)||deliveryKey.Length>256)throw new ArgumentException("写操作需要明确的1..256字符发送标识；超时不能自动重发。");
                var endpoint=reference.Transport==ModbusTransport.Tcp?$"{entry.Options.Tcp.Host}:{entry.Options.Tcp.Port}":entry.Options.Serial.PortName;
                var key=(nodeId,endpoint,deliveryKey);lock(_writeGate){if(!_writes.ContainsKey(key)&&_writes.Count>=10000)throw new InvalidOperationException("写尝试记录10000条已满；未发送。");if(!_writes.TryAdd(key,0)){Log(reference.Id,request.Operation,"DuplicateSuppressed",clock.Elapsed.TotalMilliseconds);return new(Guid.NewGuid(),request.Operation,request.UnitId,request.Address,null,new([]),new([]),clock.Elapsed.TotalMilliseconds,false,true);}}
            }
            byte[] response;ushort? transaction=null;
            if(reference.Transport==ModbusTransport.Tcp)
            {
                var id=unchecked(++entry.Transaction);transaction=id;var frame=new byte[pdu.Length+7];BinaryPrimitives.WriteUInt16BigEndian(frame,id);BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4),(ushort)(pdu.Length+1));frame[6]=request.UnitId;pdu.CopyTo(frame,7);
                attempted=true;await entry.Stream.WriteAsync(frame,deadline.Token).ConfigureAwait(false);var header=new byte[7];await ReadExactly(entry.Stream,header,deadline.Token).ConfigureAwait(false);
                var length=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));if(BinaryPrimitives.ReadUInt16BigEndian(header)!=id||BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2))!=0||header[6]!=request.UnitId||length is <2 or >254)throw new ModbusProtocolException("MBAP事务ID、协议ID、UnitId或报文长度不符；连接失同步，已关闭。",pdu[0]);
                response=new byte[length-1];await ReadExactly(entry.Stream,response,deadline.Token).ConfigureAwait(false);
            }
            else
            {
                // Exact length/CRC validation plus conservative t3.5 interval. Managed scheduling is NOT physical bus timing certification.
                var bits=1+8+(entry.Options.Serial.Parity==System.IO.Ports.Parity.None?0:1)+(entry.Options.Serial.StopBits==System.IO.Ports.StopBits.Two?2:1);
                var gap=entry.Options.Serial.BaudRate>19200?.00175:3.5*bits/entry.Options.Serial.BaudRate;var since=entry.LastCompleted==0?double.MaxValue:Stopwatch.GetElapsedTime(entry.LastCompleted).TotalSeconds;
                if(since<gap)await Task.Delay(TimeSpan.FromSeconds(gap-since),deadline.Token).ConfigureAwait(false);
                await using var abort=new SerialCancellation(entry.Serial!,deadline.Token);var frame=ModbusCodec.RtuFrame(request.UnitId,pdu);attempted=true;await entry.Stream.WriteAsync(frame,deadline.Token).ConfigureAwait(false);await entry.Stream.FlushAsync(deadline.Token).ConfigureAwait(false);
                var head=new byte[3];await ReadExactly(entry.Stream,head,deadline.Token).ConfigureAwait(false);int total=(head[1]&0x80)!=0?5:head[1] is 1 or 2 or 3 or 4?head[2]+5:8;
                if(total is <5 or >256)throw new ModbusProtocolException("RTU帧长度超过协议边界。",pdu[0]);var received=new byte[total];head.CopyTo(received,0);await ReadExactly(entry.Stream,received.AsMemory(3),deadline.Token).ConfigureAwait(false);response=ModbusCodec.CheckRtu(request.UnitId,pdu[0],received);
            }
            var parsed=ModbusCodec.Response(request,response);entry.LastCompleted=Stopwatch.GetTimestamp();Log(reference.Id,request.Operation,"ResponseVerified",clock.Elapsed.TotalMilliseconds);return new(Guid.NewGuid(),request.Operation,request.UnitId,request.Address,transaction,parsed.Coils,parsed.Registers,clock.Elapsed.TotalMilliseconds,true);
        }
        catch(OperationCanceledException ex)
        {Log(reference.Id,request.Operation,write&&attempted?"WriteUncertain":"CancelledOrTimeout",clock.Elapsed.TotalMilliseconds);if(attempted)await entry.Close();if(write&&attempted)throw new ModbusWriteUncertainException("Modbus写操作中断/超时：执行状态不确定，未自动重发；需人工核对并使用新的发送标识。",ex);if(!ct.IsCancellationRequested&&!_lifetime.IsCancellationRequested)throw new TimeoutException("Modbus事务或排队超时；未重试，已发送事务的连接已关闭以隔离迟到响应。",ex);throw;}
        catch(ModbusProtocolException ex)when(ex.ExceptionCode is not null){entry.LastCompleted=Stopwatch.GetTimestamp();Log(reference.Id,request.Operation,"ProtocolException",clock.Elapsed.TotalMilliseconds,ex.ExceptionCode);throw;}
        catch(Exception ex)when(attempted){Log(reference.Id,request.Operation,write?"WriteUncertain":"DisconnectedOrProtocolError",clock.Elapsed.TotalMilliseconds);await entry.Close();if(write)throw new ModbusWriteUncertainException("Modbus写操作未得到可验证回显：执行状态不确定；未自动重试。",ex);throw;}
        finally{if(acquired)entry.Gate.Release();}
    }
    private static async Task ReadExactly(Stream stream,Memory<byte> buffer,CancellationToken ct)
    {var offset=0;while(offset<buffer.Length){ct.ThrowIfCancellationRequested();var read=await stream.ReadAsync(buffer[offset..],ct).ConfigureAwait(false);if(read==0)throw new EndOfStreamException("远端在Modbus完整报文前断开。");offset+=read;}}
    public async ValueTask DisposeAsync()
    {if(Interlocked.Exchange(ref _disposed,1)!=0)return;_lifetime.Cancel();List<Exception>? failures=null;foreach(var e in _entries.Values)try{await e.Close();}catch(Exception ex){(failures??=[]).Add(ex);}_entries.Clear();_lifetime.Dispose();if(failures is not null)throw new AggregateException(failures);}
}

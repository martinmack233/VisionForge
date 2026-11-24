using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Communication.Tcp;

public sealed record TcpConnectionReference(Guid SessionId,string Id)
{
    public Guid Generation{get;init;}
    // Preserve legacy named-reference value equality; epoch is checked explicitly by the service.
    public bool Equals(TcpConnectionReference? other)=>other is not null&&SessionId==other.SessionId&&Id==other.Id;
    public override int GetHashCode()=>HashCode.Combine(SessionId,Id);
}
public sealed record TcpServerReference(Guid SessionId,string Id,int BoundPort)
{
    public Guid Generation{get;init;}
    public bool Equals(TcpServerReference? other)=>other is not null&&SessionId==other.SessionId&&Id==other.Id&&BoundPort==other.BoundPort;
    public override int GetHashCode()=>HashCode.Combine(SessionId,Id,BoundPort);
}
public sealed record CommunicationEvent(DateTimeOffset Timestamp, string Operation, string ResourceId,
    CommunicationState State, int BytesTransferred = 0, string? ErrorCode = null);

public sealed record TcpConnectionOptions
{
    [DisplayName("目标 IP")] public string Host { get; init; } = "127.0.0.1";
    [DisplayName("目标端口"), Range(1, 65535)] public int Port { get; init; } = 9000;
    [DisplayName("连接超时 ms"), Range(1, 600000)] public int ConnectTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("读取超时 ms"), Range(1, 600000)] public int ReadTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("写入超时 ms"), Range(1, 600000)] public int WriteTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("消息协议")] public FrameProtocolOptions Protocol { get; init; } = new();
}
public sealed record TcpListenOptions
{
    [DisplayName("监听 IP")] public string LocalAddress { get; init; } = "127.0.0.1";
    [DisplayName("监听端口（0自动分配）"), Range(0, 65535)] public int Port { get; init; }
    [DisplayName("接受连接超时 ms"), Range(1, 600000)] public int AcceptTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("读取超时 ms"), Range(1, 600000)] public int ReadTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("写入超时 ms"), Range(1, 600000)] public int WriteTimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("消息协议")] public FrameProtocolOptions Protocol { get; init; } = new();
}

/// <summary>Project/run-session-owned services and bounded audit metadata. No payload is logged.</summary>
public sealed class TcpRuntime : IDisposable
{
    private readonly Guid _sessionId;
    private readonly CancellationToken _lifetime;
    private readonly object _resourceGate=new();private readonly HashSet<string> _pendingAccepts=[];
    public const int MaximumConnections=64,MaximumListeners=16;
    private readonly ConcurrentDictionary<string, TcpChannel> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TcpListenerService> _servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Endpoint, string NodeId, string Key), byte> _deliveryAttempts = new();
    private readonly ConcurrentQueue<CommunicationEvent> _events = new();
    private int _disposed;
    public IReadOnlyList<CommunicationEvent> Events => _events.ToArray();
    public int OpenConnectionCount => _connections.Values.Count(c => !c.IsClosed);
    public int ListeningCount => _servers.Count;
    public static TcpRuntime For(IExecutionServices? services)
    {
        if (services is null) throw new InvalidOperationException("Communication nodes require a Runtime execution session.");
        return services.GetOrCreate("vision.tcp", () => new TcpRuntime(services.SessionId, services.LifetimeCancellation));
    }
    public TcpRuntime(Guid sessionId, CancellationToken lifetime = default) { _sessionId = sessionId; _lifetime = lifetime; }
    public static IReadOnlyList<string> Validate(TcpConnectionOptions? options)
    {
        if (options is null) return ["TCP connection options are required."];
        var errors = FrameCodec.Validate(options.Protocol).ToList();
        if (!IPAddress.TryParse(options.Host, out _)) errors.Add("Host must be an IPv4 or IPv6 address.");
        if (options.Port is < 1 or > 65535) errors.Add("TCP port must be in [1,65535].");
        ValidateTimeout(options.ConnectTimeoutMilliseconds, errors); ValidateTimeout(options.ReadTimeoutMilliseconds, errors); ValidateTimeout(options.WriteTimeoutMilliseconds, errors);
        return errors;
    }
    public static IReadOnlyList<string> Validate(TcpListenOptions? options)
    {
        if (options is null) return ["TCP listener options are required."];
        var errors = FrameCodec.Validate(options.Protocol).ToList();
        if (!IPAddress.TryParse(options.LocalAddress, out _)) errors.Add("LocalAddress must be an IPv4 or IPv6 address.");
        if (options.Port is < 0 or > 65535) errors.Add("Listen port must be in [0,65535].");
        ValidateTimeout(options.AcceptTimeoutMilliseconds, errors); ValidateTimeout(options.ReadTimeoutMilliseconds, errors); ValidateTimeout(options.WriteTimeoutMilliseconds, errors);
        return errors;
    }
    private static void ValidateTimeout(int value, List<string> errors)
    { if (value is < 1 or > 600000) errors.Add("Timeouts must be in [1,600000] milliseconds."); }
    private static void RequireValid(IReadOnlyList<string> errors) { if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors)); }
    private void CheckAlive() { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); _lifetime.ThrowIfCancellationRequested(); }
    private static void RequireId(string id) { if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Resource name must have 1 to 128 nonblank characters."); }
    private void CheckSession(Guid id) { CheckAlive(); if (id != _sessionId) throw new ArgumentException("Communication reference belongs to a different execution session."); }
    private void Log(string operation, string resource, CommunicationState state, int bytes = 0, string? error = null)
    { _events.Enqueue(new(DateTimeOffset.UtcNow, operation, resource, state, bytes, error)); while (_events.Count > 256) _events.TryDequeue(out _); }

    public async Task<TcpConnectionReference> ConnectAsync(string id, TcpConnectionOptions options, CancellationToken ct = default)
    {
        CheckAlive(); RequireId(id); RequireValid(Validate(options));
        if (_connections.TryGetValue(id, out var prior) && prior.IsClosed) { _connections.TryRemove(id, out _); ForgetChild(id, prior); prior.Dispose(); }
        TcpChannel channel;lock(_resourceGate){if(!_connections.TryGetValue(id,out channel!)){if(_connections.Count+_pendingAccepts.Count>=MaximumConnections)throw new InvalidOperationException("TCP连接预算64已满，请显式关闭连接。");channel=new TcpChannel(options,_lifetime);_connections[id]=channel;}}
        if (channel.Options != options) throw new InvalidOperationException("Connection parameters changed; disconnect this resource before reconnecting.");
        try { await channel.ConnectAsync(ct).ConfigureAwait(false); CheckAlive(); Log("Connect", id, CommunicationState.Connected); return new(_sessionId, id){Generation=channel.Generation}; }
        catch (Exception ex) { Log("Connect", id, CommunicationState.Failed, error: ex.GetType().Name); channel.Dispose(); throw; }
    }
    public TcpServerReference Start(string id, TcpListenOptions options)
    {
        CheckAlive(); RequireId(id); RequireValid(Validate(options));
        TcpListenerService server;lock(_resourceGate){if(!_servers.TryGetValue(id,out server!)){if(_servers.Count>=MaximumListeners)throw new InvalidOperationException("TCP监听预算16已满。");server=new TcpListenerService(options);_servers[id]=server;}}
        if (server.Options != options) throw new InvalidOperationException("Listener parameters changed; stop it before restarting.");
        try { server.Start(); CheckAlive(); Log("Listen", id, CommunicationState.Listening); return new(_sessionId, id, server.BoundPort){Generation=server.Generation}; }
        catch (Exception ex) { _servers.TryRemove(id, out _); server.Dispose(); Log("Listen", id, CommunicationState.Failed, error: ex.GetType().Name); throw; }
    }
    public async Task<TcpConnectionReference> AcceptAsync(TcpServerReference reference, string connectionId, CancellationToken ct = default)
    {
        CheckSession(reference.SessionId); RequireId(connectionId);
        if (!_servers.TryGetValue(reference.Id, out var server)||reference.Generation!=Guid.Empty&&reference.Generation!=server.Generation) throw new InvalidOperationException("TCP listener is stopped or unavailable.");
        if (_connections.TryGetValue(connectionId, out var old) && !old.IsClosed) throw new InvalidOperationException("Accepted connection name is already in use; disconnect it first.");
        lock(_resourceGate){if(_connections.Count+_pendingAccepts.Count>=MaximumConnections)throw new InvalidOperationException("TCP连接/接入预算64已满。");if(!_pendingAccepts.Add(connectionId))throw new InvalidOperationException("同名TCP连接已有接受操作，禁止抢用。");}
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime); deadline.CancelAfter(server.Options.AcceptTimeoutMilliseconds);
        TcpClient? acceptedClient = null; TcpChannel? acceptedChannel = null; var published = false;
        try
        {
            acceptedClient = await server.Listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            CheckAlive(); acceptedChannel = new TcpChannel(acceptedClient, server.Options, _lifetime);
            if (_connections.TryRemove(connectionId, out old)) { ForgetChild(connectionId, old); old.Dispose(); }
            if (!_connections.TryAdd(connectionId, acceptedChannel)) throw new InvalidOperationException("Accepted connection name is already in use.");
            if (!server.RegisterChild(connectionId, acceptedChannel)) throw new InvalidOperationException("TCP listener stopped before this client could be registered.");
            CheckAlive(); published = true;
            Log("Accept", connectionId, CommunicationState.Connected); return new(_sessionId, connectionId){Generation=acceptedChannel.Generation};
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { Log("Accept", reference.Id, CommunicationState.Failed, error: "Timeout"); throw new TimeoutException("TCP accept timed out."); }
        catch (OperationCanceledException) { Stop(reference); throw; }
        catch (Exception ex) { Log("Accept", reference.Id, CommunicationState.Failed, error: ex.GetType().Name); throw; }
        finally
        {
            lock(_resourceGate)_pendingAccepts.Remove(connectionId);
            if (!published)
            {
                acceptedChannel?.Dispose(); acceptedClient?.Dispose();
                if (acceptedChannel is not null) ((ICollection<KeyValuePair<string, TcpChannel>>)_connections).Remove(new(connectionId, acceptedChannel));
            }
        }
    }
    public async Task<CommunicationStatus> SendAsync(TcpConnectionReference reference, string nodeId, string deliveryKey,
        string message, bool sendEnabled, bool forceResend = false, CancellationToken ct = default, int? writeTimeoutMilliseconds = null)
    {
        CheckSession(reference.SessionId);
        if (!sendEnabled) { Log("Send", reference.Id, CommunicationState.Disabled); return new(CommunicationState.Disabled, false, "Sending is disabled; explicitly enable it to transmit."); }
        if (writeTimeoutMilliseconds is < 1 or > 600000) throw new ArgumentException("Write timeout must be in [1,600000] milliseconds.");
        if (string.IsNullOrWhiteSpace(deliveryKey) || deliveryKey.Length > 256) throw new ArgumentException("Enabled sends require a nonblank DeliveryKey of at most 256 characters.");
        var channel = Channel(reference); var bytes = FrameCodec.Encode(message, channel.Options.Protocol);
        ct.ThrowIfCancellationRequested();
        var identity = (channel.EndpointIdentity, nodeId, deliveryKey);
        if (!_deliveryAttempts.ContainsKey(identity) && _deliveryAttempts.Count >= 10000)
            throw new InvalidOperationException("TCP delivery history reached 10000 keys; no message was sent. Start a new explicit project session and preserve business-level receiver deduplication.");
        if (!forceResend && !_deliveryAttempts.TryAdd(identity, 0))
        { Log("Send", reference.Id, CommunicationState.DuplicateSuppressed); return new(CommunicationState.DuplicateSuppressed, true, "This delivery key was already attempted; no bytes were sent."); }
        _deliveryAttempts.TryAdd(identity, 0);
        try
        {
            await channel.WriteAsync(bytes, ct, writeTimeoutMilliseconds).ConfigureAwait(false); Log("Send", reference.Id, CommunicationState.Sent, bytes.Length);
            return new(CommunicationState.Sent, true, "Message sent.", bytes.Length);
        }
        catch (Exception ex) { Log("Send", reference.Id, CommunicationState.Failed, error: ex.GetType().Name); throw; }
    }
    public async Task<string> ReceiveAsync(TcpConnectionReference reference, CancellationToken ct = default, int? readTimeoutMilliseconds = null)
    {
        if (readTimeoutMilliseconds is < 1 or > 600000) throw new ArgumentException("Read timeout must be in [1,600000] milliseconds.");
        var channel = Channel(reference);
        try
        {
            var message = await channel.ReadAsync(ct, readTimeoutMilliseconds).ConfigureAwait(false);
            Log("Receive", reference.Id, CommunicationState.Received, FrameCodec.GetEncoding(channel.Options.Protocol.Encoding).GetByteCount(message)); return message;
        }
        catch (Exception ex) { Log("Receive", reference.Id, CommunicationState.Failed, error: ex.GetType().Name); throw; }
    }
    public bool IsServerConnection(TcpConnectionReference reference)=>Channel(reference).AcceptedByServer;
    public async Task<CommunicationStatus> SendBytesAsync(TcpConnectionReference reference,string nodeId,string key,byte[] message,bool enabled,bool forceResend=false,CancellationToken ct=default,int? timeout=null)
    {
        CheckSession(reference.SessionId);if(!enabled)return new(CommunicationState.Disabled,false,"发送未启用；没有发送数据。");var channel=Channel(reference);var framed=FrameCodec.EncodeBytes(message,channel.Options.Protocol);ValidateOverride(timeout);
        if(!RegisterAttempt(channel,nodeId,key,forceResend))return new(CommunicationState.DuplicateSuppressed,true,"同会话发送标识已尝试，未再次发送。");
        await channel.WriteAsync(framed,ct,timeout).ConfigureAwait(false);Log("SendBinary",reference.Id,CommunicationState.Sent,message.Length);return new(CommunicationState.Sent,true,"二进制帧已写入；不代表业务确认。",message.Length);
    }
    public async Task<byte[]> ReceiveBytesAsync(TcpConnectionReference reference,CancellationToken ct=default,int? timeout=null)
    {ValidateOverride(timeout);var channel=Channel(reference);if(channel.Options.Protocol.Mode!=MessageBoundaryMode.LengthPrefix)throw new ArgumentException("二进制接收需要显式选择4字节长度前缀协议。");var bytes=await channel.ReadBytesAsync(ct,timeout).ConfigureAwait(false);Log("ReceiveBinary",reference.Id,CommunicationState.Received,bytes.Length);return bytes;}
    public async Task<(CommunicationStatus Status,byte[]? Response)> ExchangeAsync(TcpConnectionReference reference,string nodeId,string key,byte[] message,bool binary,bool enabled,bool forceResend=false,CancellationToken ct=default,int? writeTimeout=null,int? readTimeout=null)
    {
        CheckSession(reference.SessionId);if(!enabled)return(new(CommunicationState.Disabled,false,"请求发送未启用，没有发送或接收。"),null);ValidateOverride(writeTimeout);ValidateOverride(readTimeout);var channel=Channel(reference);
        var framed=binary?FrameCodec.EncodeBytes(message,channel.Options.Protocol):FrameCodec.Encode(FrameCodec.GetEncoding(channel.Options.Protocol.Encoding).GetString(message),channel.Options.Protocol);
        if(!RegisterAttempt(channel,nodeId,key,forceResend))return(new(CommunicationState.DuplicateSuppressed,true,"请求已尝试，不重发也不消费旧响应。"),null);
        var response=await channel.ExchangeAsync(framed,ct,writeTimeout,readTimeout).ConfigureAwait(false);Log("RequestResponse",reference.Id,CommunicationState.Received,response.Length);return(new(CommunicationState.Received,true,"收到完整协议帧；业务有效性需由下游判断。",response.Length),response);
    }
    public string Decode(TcpConnectionReference reference,byte[] bytes)=>FrameCodec.GetEncoding(Channel(reference).Options.Protocol.Encoding).GetString(bytes);
    public byte[] Encode(TcpConnectionReference reference,string text)=>FrameCodec.GetEncoding(Channel(reference).Options.Protocol.Encoding).GetBytes(text);
    private static void ValidateOverride(int? value){if(value is <1 or >600000)throw new ArgumentException("TCP超时应为1..600000ms。");}
    private bool RegisterAttempt(TcpChannel channel,string node,string key,bool force)
    {
        if(string.IsNullOrWhiteSpace(key)||key.Length>256)throw new ArgumentException("明确发送需要1..256字符DeliveryKey。");var identity=(channel.EndpointIdentity,node,key);
        if(!_deliveryAttempts.ContainsKey(identity)&&_deliveryAttempts.Count>=10000)throw new InvalidOperationException("发送记录预算10000已满；未发送。");if(!force&&!_deliveryAttempts.TryAdd(identity,0))return false;_deliveryAttempts.TryAdd(identity,0);return true;
    }
    private TcpChannel Channel(TcpConnectionReference reference)
    {
        CheckSession(reference.SessionId);
        if (!_connections.TryGetValue(reference.Id, out var channel) || channel.IsClosed||reference.Generation!=Guid.Empty&&reference.Generation!=channel.Generation) throw new InvalidOperationException("TCP connection is closed or unavailable.");
        return channel;
    }
    public void Disconnect(TcpConnectionReference reference)
    {
        CheckSession(reference.SessionId);
        if(reference.Generation!=Guid.Empty&&_connections.TryGetValue(reference.Id,out var oldReference)&&oldReference.Generation!=reference.Generation)throw new InvalidOperationException("TCP引用已过期，未关闭新的连接。");
        if (_connections.TryRemove(reference.Id, out var connection)) { ForgetChild(reference.Id, connection); connection.Dispose(); }
        Log("Disconnect", reference.Id, CommunicationState.Closed);
    }
    public void Stop(TcpServerReference reference)
    {
        CheckSession(reference.SessionId);
        if(reference.Generation!=Guid.Empty&&_servers.TryGetValue(reference.Id,out var existing)&&reference.Generation!=existing.Generation)throw new InvalidOperationException("TCP服务引用已过期，未关闭新的监听。");
        if (_servers.TryRemove(reference.Id, out var server))
        {
            server.Dispose();
            foreach (var pair in server.Children)
            {
                ((ICollection<KeyValuePair<string, TcpChannel>>)_connections).Remove(pair);
                pair.Value.Dispose();
            }
        }
        Log("Stop", reference.Id, CommunicationState.Closed);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var server in _servers.Values) server.Dispose(); _servers.Clear();
        foreach (var channel in _connections.Values) channel.Dispose(); _connections.Clear();
    }
    private void ForgetChild(string id, TcpChannel channel)
    {
        foreach (var server in _servers.Values)
            ((ICollection<KeyValuePair<string, TcpChannel>>)server.Children).Remove(new(id, channel));
    }

    private sealed class TcpListenerService(TcpListenOptions options) : IDisposable
    {
        private readonly object _gate = new(); private bool _started, _closed;
        public Guid Generation{get;}=Guid.NewGuid();
        public TcpListenOptions Options { get; } = options;
        public TcpListener Listener { get; } = new(IPAddress.Parse(options.LocalAddress), options.Port);
        public ConcurrentDictionary<string, TcpChannel> Children { get; } = new();
        public int BoundPort => ((IPEndPoint)Listener.LocalEndpoint).Port;
        public void Start() { lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); if (!_started) { Listener.Start(16); _started = true; } } }
        public bool RegisterChild(string id, TcpChannel channel)
        { lock (_gate) { if (_closed || !_started) return false; Children[id] = channel; return true; } }
        public void Dispose()
        {
            lock (_gate) { _closed = true; Listener.Stop(); _started = false; foreach (var child in Children.Values) child.Dispose(); }
        }
    }

    private sealed class TcpChannel : IDisposable
    {
        private readonly CancellationToken _lifetime;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _connectGate = new(1), _readGate = new(1), _writeGate = new(1),_transactionGate=new(1);
        public Guid Generation{get;}=Guid.NewGuid();
        public bool AcceptedByServer{get;}
        private TcpClient? _client;
        private FramedMessageReader? _reader;
        private int _closed;
        public TcpConnectionOptions Options { get; }
        public bool IsClosed => Volatile.Read(ref _closed) != 0;
        public string EndpointIdentity { get; }
        public TcpChannel(TcpConnectionOptions options, CancellationToken lifetime)
        { Options = options; _lifetime = lifetime; EndpointIdentity = new IPEndPoint(IPAddress.Parse(options.Host), options.Port).ToString(); }
        public TcpChannel(TcpClient client, TcpListenOptions options, CancellationToken lifetime)
        {
            _client = client; _lifetime = lifetime;AcceptedByServer=true;
            Options = new() { Host = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(), Port = ((IPEndPoint)client.Client.RemoteEndPoint!).Port,
                ReadTimeoutMilliseconds = options.ReadTimeoutMilliseconds, WriteTimeoutMilliseconds = options.WriteTimeoutMilliseconds, Protocol = options.Protocol };
            EndpointIdentity = client.Client.RemoteEndPoint!.ToString()!;
            _reader = new(client.GetStream(), Options.Protocol);
        }
        public async Task ConnectAsync(CancellationToken ct)
        {
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_stateGate)
                {
                    ObjectDisposedException.ThrowIf(IsClosed, this);
                    if (_client is not null) return;
                    _client = new TcpClient(IPAddress.Parse(Options.Host).AddressFamily);
                }
                using var deadline = Deadline(ct, Options.ConnectTimeoutMilliseconds);
                try { await _client.ConnectAsync(IPAddress.Parse(Options.Host), Options.Port, deadline.Token).ConfigureAwait(false); _reader = new(_client.GetStream(), Options.Protocol); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) { Dispose(); throw new TimeoutException("TCP connect timed out."); }
                catch { Dispose(); throw; }
            }
            finally { _connectGate.Release(); }
        }
        private async Task WriteCoreAsync(byte[] bytes, CancellationToken ct, int? timeout = null)
        {
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(IsClosed, this); using var deadline = Deadline(ct, timeout ?? Options.WriteTimeoutMilliseconds);
                try { await _client!.GetStream().WriteAsync(bytes, deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) { Dispose(); throw new TimeoutException("TCP write timed out; delivery is uncertain. No automatic retry was made."); }
                catch { Dispose(); throw; }
            }
            finally { _writeGate.Release(); }
        }
        private async Task<string> ReadCoreAsync(CancellationToken ct, int? timeout = null)
        {
            await _readGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(IsClosed, this); using var deadline = Deadline(ct, timeout ?? Options.ReadTimeoutMilliseconds);
                try { return await _reader!.ReadAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) { Dispose(); throw new TimeoutException("TCP read timed out; connection closed to discard any partial frame."); }
                catch { Dispose(); throw; }
            }
            finally { _readGate.Release(); }
        }
        public async Task WriteAsync(byte[] bytes,CancellationToken ct,int? timeout=null){await WaitTransactionAsync(ct,timeout??Options.WriteTimeoutMilliseconds).ConfigureAwait(false);try{await WriteCoreAsync(bytes,ct,timeout).ConfigureAwait(false);}finally{_transactionGate.Release();}}
        public async Task<string> ReadAsync(CancellationToken ct,int? timeout=null){await WaitTransactionAsync(ct,timeout??Options.ReadTimeoutMilliseconds).ConfigureAwait(false);try{return await ReadCoreAsync(ct,timeout).ConfigureAwait(false);}finally{_transactionGate.Release();}}
        public async Task<byte[]> ReadBytesAsync(CancellationToken ct,int? timeout=null){await WaitTransactionAsync(ct,timeout??Options.ReadTimeoutMilliseconds).ConfigureAwait(false);try{return await ReadBytesCoreAsync(ct,timeout).ConfigureAwait(false);}finally{_transactionGate.Release();}}
        private async Task<byte[]> ReadBytesCoreAsync(CancellationToken ct,int? timeout=null)
        {
            ObjectDisposedException.ThrowIf(IsClosed,this);using var deadline=Deadline(ct,timeout??Options.ReadTimeoutMilliseconds);
            try{return await _reader!.ReadBytesAsync(deadline.Token).ConfigureAwait(false);}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested&&!_lifetime.IsCancellationRequested){Dispose();throw new TimeoutException("TCP接收超时，连接已关闭以丢弃部分/迟到响应。");}
            catch{Dispose();throw;}
        }
        public async Task<byte[]> ExchangeAsync(byte[] framed,CancellationToken ct,int? writeTimeout=null,int? readTimeout=null)
        {await WaitTransactionAsync(ct,writeTimeout??Options.WriteTimeoutMilliseconds).ConfigureAwait(false);try{await WriteCoreAsync(framed,ct,writeTimeout).ConfigureAwait(false);return await ReadBytesCoreAsync(ct,readTimeout).ConfigureAwait(false);}finally{_transactionGate.Release();}}
        // Waiting for another complete transaction is bounded separately from actual IO.
        // A queued timeout has not touched the stream and must not abort the active owner's IO.
        private async Task WaitTransactionAsync(CancellationToken ct,int timeout)
        {using var deadline=Deadline(ct,timeout);try{await _transactionGate.WaitAsync(deadline.Token).ConfigureAwait(false);}catch(OperationCanceledException)when(!ct.IsCancellationRequested&&!_lifetime.IsCancellationRequested){throw new TimeoutException("TCP事务排队超时；本次尚未读写，不关闭正在使用的连接。");}}
        private CancellationTokenSource Deadline(CancellationToken ct, int timeout)
        { var result = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime); result.CancelAfter(timeout); return result; }
        public void Dispose() { lock (_stateGate) { if (Interlocked.Exchange(ref _closed, 1) == 0) _client?.Dispose(); } }
    }
}

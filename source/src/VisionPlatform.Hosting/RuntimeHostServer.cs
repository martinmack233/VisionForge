using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Hosting;

/// <summary>Explicit persistent TCP service. Each connection owns an isolated project/run session.</summary>
public sealed class RuntimeHostServer : IAsyncDisposable
{
    private readonly NodeRegistry _registry;
    private readonly RuntimeHostOptions _options;
    private readonly IReadOnlyDictionary<string, string>? _versions;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, (TcpClient Client, Task Completion)> _clients = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _stopTask;
    private readonly object _gate = new();
    private long _sequence;
    public int BoundPort { get; private set; }
    public int ActiveConnections => _clients.Count;
    public RuntimeHostServer(NodeRegistry registry, RuntimeHostOptions? options = null,
        IReadOnlyDictionary<string, string>? algorithmVersions = null, ILogger? logger = null)
    { _registry = registry; _options = options ?? new(); _options.Validate(); _versions = algorithmVersions; _logger = logger ?? NullLogger.Instance; }
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_acceptTask is not null || _stopTask is not null) throw new InvalidOperationException("A host server instance can be started only once.");
            _listener = new(IPAddress.Parse(_options.ListenAddress), _options.Port);
            try { _listener.Start(_options.MaximumClients); BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port; }
            catch { _listener.Stop(); _listener = null; throw; }
            _acceptTask = AcceptAsync(ct); return Task.CompletedTask;
        }
    }
    private async Task AcceptAsync(CancellationToken startupLifetime)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, startupLifetime);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                if (_clients.Count >= _options.MaximumClients) { client.Dispose(); continue; }
                client.NoDelay = true; var id = Interlocked.Increment(ref _sequence);
                var begin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () => { await begin.Task.ConfigureAwait(false); await ServeAsync(id, client, linked.Token).ConfigureAwait(false); });
                _clients[id] = (client, task); begin.SetResult();
            }
        }
        catch (Exception ex) when (linked.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { _listener?.Stop(); _lifetime.Cancel(); BoundPort = 0; }
    }
    private FrameProtocolOptions Protocol => new() { Mode = MessageBoundaryMode.LengthPrefix, Encoding = MessageEncoding.Utf8, MaximumMessageBytes = _options.MaximumMessageBytes };
    private async Task ServeAsync(long id, TcpClient client, CancellationToken ct)
    {
        await using var session = new RuntimeHostSession(_registry, _versions, _logger);
        try
        {
            using var stream = client.GetStream(); var reader = new FramedMessageReader(stream, Protocol);
            while (!ct.IsCancellationRequested && !session.IsClosed)
            {
                string message;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    read.CancelAfter(_options.ReadTimeoutMilliseconds);
                    try { message = await reader.ReadAsync(read.Token).ConfigureAwait(false); }
                    catch (InvalidDataException) { await WriteAsync(stream, HostJson.Error("", HostErrorCodes.InvalidRequest, "Incoming frame exceeds the configured message budget."), ct).ConfigureAwait(false); break; }
                    catch (DecoderFallbackException) { await WriteAsync(stream, HostJson.Error("", HostErrorCodes.InvalidRequest, "Incoming frame is not valid UTF8."), ct).ConfigureAwait(false); break; }
                }
                HostResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<HostRequest>(message, HostJson.Options) ?? throw new JsonException("Request is null.");
                    response = await session.HandleAsync(request, ct).ConfigureAwait(false);
                }
                catch (JsonException ex) { response = HostJson.Error("", HostErrorCodes.InvalidRequest, ex.Message); }
                await WriteAsync(stream, response, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { _logger.LogInformation("Host connection {Connection} ended ({Type})", id, ex.GetType().Name); }
        catch (Exception ex) { _logger.LogError("Host connection {Connection} failed ({Type})", id, ex.GetType().Name); }
        finally
        {
            // Dispose the execution scope before reporting a connection as released.
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError("Host connection {Connection} cleanup failed ({Type})", id, ex.GetType().Name); }
            finally { client.Dispose(); _clients.TryRemove(id, out _); }
        }
    }
    private async Task WriteAsync(NetworkStream stream, HostResponse response, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(response, HostJson.Options);
        if (FrameCodec.GetEncoding(MessageEncoding.Utf8).GetByteCount(json) > _options.MaximumMessageBytes)
            json = JsonSerializer.Serialize(HostJson.Error(response.RequestId, HostErrorCodes.ResultTooLarge, "The structured result exceeds the configured wire budget; narrow the graph/ROI or request a target node."), HostJson.Options);
        var bytes = FrameCodec.Encode(json, Protocol);
        using var write = CancellationTokenSource.CreateLinkedTokenSource(ct); write.CancelAfter(_options.WriteTimeoutMilliseconds);
        await stream.WriteAsync(bytes, write.Token).ConfigureAwait(false);
    }
    public Task StopAsync()
    { lock (_gate) return _stopTask ??= StopCoreAsync(); }
    private async Task StopCoreAsync()
    {
        _lifetime.Cancel(); _listener?.Stop();
        foreach (var entry in _clients.Values) entry.Client.Dispose();
        if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false);
        // Accept loop has stopped, so no new sessions can race this snapshot.
        await Task.WhenAll(_clients.Values.Select(c => c.Completion)).ConfigureAwait(false);
        BoundPort = 0;
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _lifetime.Dispose(); }
}

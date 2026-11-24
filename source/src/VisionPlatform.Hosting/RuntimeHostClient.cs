using System.Net.Sockets;
using System.Text.Json;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Contracts.Hosting;

namespace VisionPlatform.Hosting;

/// <summary>Protocol test/application helper; no algorithm engine and no automatic retries.</summary>
public sealed class RuntimeHostClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _exchange = new(1, 1);
    private NetworkStream? _stream;
    private FramedMessageReader? _reader;
    private readonly FrameProtocolOptions _protocol = new() { Mode = MessageBoundaryMode.LengthPrefix, Encoding = MessageEncoding.Utf8, MaximumMessageBytes = 1048576 };
    private readonly int _timeoutMilliseconds;
    public RuntimeHostClient(int timeoutMilliseconds = 10000)
    { if (timeoutMilliseconds is < 1 or > 600000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds)); _timeoutMilliseconds = timeoutMilliseconds; }
    public async Task ConnectAsync(string address, int port, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeoutMilliseconds);
        try { await _client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false); _stream = _client.GetStream(); _reader = new(_stream, _protocol); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { _client.Dispose(); throw new TimeoutException("RuntimeHost client connection timed out."); }
        catch { _client.Dispose(); throw; }
    }
    public async Task<HostResponse> SendAsync(HostRequest request, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(_timeoutMilliseconds);
        var entered = false;
        try
        {
            await _exchange.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            if (_stream is null || _reader is null) throw new InvalidOperationException("Connect the client before sending commands.");
            await _stream.WriteAsync(FrameCodec.Encode(JsonSerializer.Serialize(request, HostJson.Options), _protocol), deadline.Token).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<HostResponse>(await _reader.ReadAsync(deadline.Token).ConfigureAwait(false), HostJson.Options) ?? throw new InvalidDataException("Host response was null.");
            if (response.ProtocolVersion != 1 || response.RequestId != request.RequestId) throw new InvalidDataException("RuntimeHost response version/request ID differs from the request.");
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { if (entered) _client.Dispose(); throw new TimeoutException("RuntimeHost client command timed out; the interrupted connection is closed."); }
        catch { if (entered) _client.Dispose(); throw; }
        finally { if (entered) _exchange.Release(); }
    }
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
}

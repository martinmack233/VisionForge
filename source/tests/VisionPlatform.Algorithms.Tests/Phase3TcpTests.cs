using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase3TcpTests
{
    [Theory]
    [InlineData(MessageBoundaryMode.Delimiter, MessageEncoding.Utf8)]
    [InlineData(MessageBoundaryMode.Delimiter, MessageEncoding.Ascii)]
    [InlineData(MessageBoundaryMode.Delimiter, MessageEncoding.Utf16LittleEndian)]
    [InlineData(MessageBoundaryMode.LengthPrefix, MessageEncoding.Utf8)]
    [InlineData(MessageBoundaryMode.LengthPrefix, MessageEncoding.Ascii)]
    [InlineData(MessageBoundaryMode.LengthPrefix, MessageEncoding.Utf16LittleEndian)]
    public async Task RealLoopbackFramingHandlesFragmentationAndCoalescedMessages(MessageBoundaryMode mode, MessageEncoding encoding)
    {
        var protocol = new FrameProtocolOptions { Mode = mode, Encoding = encoding };
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var hub = new TcpRuntime(Guid.NewGuid());
        var reference = await hub.ConnectAsync("client", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port, Protocol = protocol });
        using var peer = await listener.AcceptTcpClientAsync();
        var first = FrameCodec.Encode("OK,2,0.982", protocol); var second = FrameCodec.Encode("NG,3,0.100", protocol);
        var combined = first.Concat(second).ToArray();
        var receive = hub.ReceiveAsync(reference);
        for (var offset = 0; offset < 5; offset++) await peer.GetStream().WriteAsync(combined.AsMemory(offset, 1));
        await peer.GetStream().WriteAsync(combined.AsMemory(5));
        Assert.Equal("OK,2,0.982", await receive.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("NG,3,0.100", await hub.ReceiveAsync(reference));
    }

    [Theory]
    [InlineData(MessageBoundaryMode.Delimiter)]
    [InlineData(MessageBoundaryMode.LengthPrefix)]
    public async Task ClientAndServerExchangeRealDataAndSuppressDuplicateKeys(MessageBoundaryMode mode)
    {
        var protocol = new FrameProtocolOptions { Mode = mode };
        using var server = new TcpRuntime(Guid.NewGuid()); using var client = new TcpRuntime(Guid.NewGuid());
        var listener = server.Start("server", new() { Protocol = protocol });
        var accept = server.AcceptAsync(listener, "accepted");
        var outbound = await client.ConnectAsync("client", new() { Port = listener.BoundPort, Protocol = protocol });
        var inbound = await accept;
        var first = await client.SendAsync(outbound, "send", "inspection-1", "OK", true);
        Assert.Equal(CommunicationState.Sent, first.State); Assert.Equal("OK", await server.ReceiveAsync(inbound));
        var duplicate = await client.SendAsync(outbound, "send", "inspection-1", "SHOULD NOT SEND", true);
        Assert.Equal(CommunicationState.DuplicateSuppressed, duplicate.State); Assert.Equal(0, duplicate.BytesTransferred);
        await client.SendAsync(outbound, "send", "inspection-1", "NG", true, forceResend: true);
        Assert.Equal("NG", await server.ReceiveAsync(inbound));
        await server.SendAsync(inbound, "response", "reply-1", "ACK", true);
        Assert.Equal("ACK", await client.ReceiveAsync(outbound));
        Assert.Equal(outbound, await client.ConnectAsync("client", new() { Port = listener.BoundPort, Protocol = protocol }));
        Assert.Equal(1, client.OpenConnectionCount);
        Assert.DoesNotContain(client.Events, e => e.ErrorCode?.Contains("SHOULD NOT SEND", StringComparison.Ordinal) == true);
        Assert.Equal(2, client.Events.Count(e => e.State == CommunicationState.Sent));
        server.Stop(listener); Assert.Equal(0, server.ListeningCount); Assert.Equal(0, server.OpenConnectionCount);
        await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync(outbound));
    }

    [Fact]
    public async Task DelimiterReaderBoundsMessagesAndRetainsEmptyFrames()
    {
        var options = new FrameProtocolOptions { MaximumMessageBytes = 3 };
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("\nOK\n"));
        var reader = new FramedMessageReader(valid, options);
        Assert.Equal("", await reader.ReadAsync()); Assert.Equal("OK", await reader.ReadAsync());
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("TOO-LONG\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FramedMessageReader(invalid, options).ReadAsync());
        using var prefix = new MemoryStream([0, 0, 0, 4]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FramedMessageReader(prefix, options with { Mode = MessageBoundaryMode.LengthPrefix }).ReadAsync());
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode("four", options));
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode("A\nB", options));
        Assert.Throws<EncoderFallbackException>(() => FrameCodec.Encode("中文", options with { Encoding = MessageEncoding.Ascii, MaximumMessageBytes = 100 }));
    }

    [Fact]
    public async Task Utf16DelimiterMatchesOnlyCodeUnitBoundariesAndRejectsMalformedUtf8()
    {
        var options = new FrameProtocolOptions { Encoding = MessageEncoding.Utf16LittleEndian };
        // 0A 00 occurs unaligned across these two code units, and must not be mistaken for newline.
        var message = "\u0A41\u4100";
        using var bytes = new MemoryStream(FrameCodec.Encode(message, options));
        Assert.Equal(message, await new FramedMessageReader(bytes, options).ReadAsync());
        using var invalid = new MemoryStream([0xFF, 0x0A]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => new FramedMessageReader(invalid, new()).ReadAsync());
    }

    [Fact]
    public async Task ReadTimeoutClosesConnectionAndDoesNotReusePartialFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var hub = new TcpRuntime(Guid.NewGuid());
        var connection = await hub.ConnectAsync("client", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port, ReadTimeoutMilliseconds = 50 });
        using var peer = await listener.AcceptTcpClientAsync(); await peer.GetStream().WriteAsync(Encoding.UTF8.GetBytes("partial"));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => hub.ReceiveAsync(connection));
        Assert.Contains("partial frame", error.Message); Assert.Equal(0, hub.OpenConnectionCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.ReceiveAsync(connection));
        Assert.Contains(hub.Events, e => e.Operation == "Receive" && e.ErrorCode == nameof(TimeoutException));
    }

    [Fact]
    public async Task CancellingActiveReadClosesConnectionAndServerAcceptCancellationReleasesPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var hub = new TcpRuntime(Guid.NewGuid());
        var connection = await hub.ConnectAsync("client", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var peer = await listener.AcceptTcpClientAsync(); using var cancellation = new CancellationTokenSource();
        var read = hub.ReceiveAsync(connection, cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); Assert.Equal(0, hub.OpenConnectionCount);
        var server = hub.Start("server", new()); using var acceptCancellation = new CancellationTokenSource();
        var accept = hub.AcceptAsync(server, "accepted", acceptCancellation.Token); acceptCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept); Assert.Equal(0, hub.ListeningCount);
        using var rebound = new TcpListener(IPAddress.Loopback, server.BoundPort); rebound.Start();
    }

    [Fact]
    public async Task ConnectFailurePortConflictAndRemoteDisconnectAreExplicit()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using var hub = new TcpRuntime(Guid.NewGuid());
        Assert.Throws<SocketException>(() => hub.Start("occupied", new() { Port = port }));
        Assert.Equal(0, hub.ListeningCount);
        occupied.Stop();
        // Windows may return WSAECONNREFUSED immediately or consume its SYN retry timeout.
        // Both are explicit bounded connection failures, rather than a successful connection.
        var failure = await Record.ExceptionAsync(() => hub.ConnectAsync("refused", new() { Port = port, ConnectTimeoutMilliseconds = 100 }));
        Assert.True(failure is SocketException or TimeoutException, failure?.ToString());
        Assert.Equal(0, hub.OpenConnectionCount);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var connection = await hub.ConnectAsync("remote", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        var accepted = await listener.AcceptTcpClientAsync(); accepted.Dispose();
        await Assert.ThrowsAnyAsync<IOException>(() => hub.ReceiveAsync(connection));
        Assert.Equal(0, hub.OpenConnectionCount);
    }

    [Fact]
    public async Task WriteTimeoutIsBoundedAndDoesNotLogMessageBody()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Server.ReceiveBufferSize = 1024; listener.Start();
        using var hub = new TcpRuntime(Guid.NewGuid());
        var connection = await hub.ConnectAsync("blackhole", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            WriteTimeoutMilliseconds = 30, Protocol = new() { Mode = MessageBoundaryMode.LengthPrefix, MaximumMessageBytes = 1048576 } });
        using var accepted = await listener.AcceptTcpClientAsync(); accepted.ReceiveBufferSize = 1024;
        var body = new string('S', 1048576); TimeoutException? timeout = null;
        for (var i = 0; i < 16 && timeout is null; i++)
            try { await hub.SendAsync(connection, "send", "blackhole-" + i, body, true); }
            catch (TimeoutException ex) { timeout = ex; }
        Assert.NotNull(timeout); Assert.Equal(0, hub.OpenConnectionCount);
        Assert.All(hub.Events, e => Assert.DoesNotContain("SSSS", e.ToString()));
    }

    [Fact]
    public async Task SessionDisposeStopsListenerAndClosesAcceptedClients()
    {
        var scope = new RuntimeExecutionSession(); var hub = TcpRuntime.For(scope);
        var listener = hub.Start("server", new()); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
        await hub.AcceptAsync(listener, "accepted");
        Assert.Equal(1, hub.OpenConnectionCount); await scope.DisposeAsync();
        Assert.Equal(0, hub.ListeningCount); Assert.Equal(0, hub.OpenConnectionCount);
        using var rebound = new TcpListener(IPAddress.Loopback, listener.BoundPort); rebound.Start();
    }

    [Fact]
    public async Task ClosingSessionInterruptsPendingReadAndAcceptWithoutLeavingListener()
    {
        var scope = new RuntimeExecutionSession(); var hub = TcpRuntime.For(scope);
        var listener = hub.Start("server", new()); using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, listener.BoundPort);
        var connection = await hub.AcceptAsync(listener, "accepted");
        var read = hub.ReceiveAsync(connection); var accept = hub.AcceptAsync(listener, "next-client");
        await scope.DisposeAsync();
        var readError = await Record.ExceptionAsync(() => read.WaitAsync(TimeSpan.FromSeconds(3)));
        var acceptError = await Record.ExceptionAsync(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.NotNull(readError); Assert.IsNotType<TimeoutException>(readError);
        Assert.NotNull(acceptError); Assert.IsNotType<TimeoutException>(acceptError);
        Assert.Equal(0, hub.ListeningCount); Assert.Equal(0, hub.OpenConnectionCount);
        using var rebound = new TcpListener(IPAddress.Loopback, listener.BoundPort); rebound.Start();
    }

    [Fact]
    public async Task HandlesCannotCrossSessionsAndDisabledSendHasNoSideEffect()
    {
        using var server = new TcpRuntime(Guid.NewGuid()); var listener = server.Start("server", new());
        using var client = new TcpRuntime(Guid.NewGuid());
        var outgoing = await client.ConnectAsync("client", new() { Port = listener.BoundPort }); var incoming = await server.AcceptAsync(listener, "accepted");
        await Assert.ThrowsAsync<ArgumentException>(() => server.SendAsync(outgoing, "send", "1", "NO", true));
        var status = await client.SendAsync(outgoing, "send", "", "NO", false);
        Assert.Equal(CommunicationState.Disabled, status.State);
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync(outgoing, "send", "", "NO", true));
        await client.SendAsync(outgoing, "send", "valid", "YES", true); Assert.Equal("YES", await server.ReceiveAsync(incoming));
    }

    [Fact]
    public async Task ProductionTcpNodesSendActualMessageAndAutomaticScopeClosesConnections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receive = Task.Run(async () => { using var peer = await listener.AcceptTcpClientAsync(); return await new FramedMessageReader(peer.GetStream(), new()).ReadAsync(); });
        var registry = BuiltinRegistration.CreateRegistry();
        var graph = new NodeGraph([
            new("connect", "TcpConnect", "connect", JsonDefaults.Element(new TcpConnectParameters { Options = new() { Port = port } })),
            new("send", "TcpSend", "send", JsonDefaults.Element(new TcpSendParameters { SendEnabled = true, DeliveryKey = "fixture-1", LiteralMessage = "OK,2,0.982" }))],
            [new("connect", "Connection", "send", "Connection")]);
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(graph);
        Assert.True(run.Success, run.ErrorMessage); Assert.Equal("OK,2,0.982", await receive.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(CommunicationState.Sent, run.Nodes["send"].Output<CommunicationStatus>("Status").State);
    }

    [Fact]
    public async Task StructuredReportFlowsDirectlyIntoProductionSendAndSecondExecutionDoesNotResend()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var scope = new RuntimeExecutionSession(); var hub = TcpRuntime.For(scope);
        var connection = await hub.ConnectAsync("client", new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port });
        using var accepted = await listener.AcceptTcpClientAsync();
        var spec = new NodeSpecification("send", "TcpSend", "report send", JsonDefaults.Element(new TcpSendParameters
        {
            SendEnabled = true, DeliveryKey = "report-1", ResultFormat = new() { Format = VisionPlatform.Algorithms.Results.ResultMessageFormat.Csv }
        }));
        var inputs = new Dictionary<string, object> { ["Connection"] = connection,
            ["Result"] = new InspectionReport { IsOk = true, BlobCount = 2, MatchScore = .982 } };
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry(), logger: null, services: scope);
        using var first = await executor.ExecuteSingleAsync(spec, inputs);
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal("OK,2,0.982", await new FramedMessageReader(accepted.GetStream(), new()).ReadAsync());
        using var second = await executor.ExecuteSingleAsync(spec, inputs);
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(CommunicationState.DuplicateSuppressed, second.Nodes["send"].Output<CommunicationStatus>("Status").State);
    }

    [Fact]
    public void ProtocolAndNodeParametersRejectInvalidValues()
    {
        Assert.NotEmpty(FrameCodec.Validate(new() { Delimiter = "" }));
        Assert.NotEmpty(FrameCodec.Validate(new() { Encoding = (MessageEncoding)999 }));
        Assert.NotEmpty(TcpRuntime.Validate(new TcpConnectionOptions { Host = "not-an-ip", Port = 0 }));
        Assert.NotEmpty(TcpRuntime.Validate(new TcpListenOptions { Port = -1, ReadTimeoutMilliseconds = 0 }));
        Assert.NotEmpty(new TcpSendNode().ValidateParameters(JsonDefaults.Element(new TcpSendParameters { ResultFormat = new() { DecimalPlaces = 99 } })));
    }
}

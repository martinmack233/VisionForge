using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Hosting;
using VisionPlatform.Hosting.Fixture;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace VisionPlatform.Hosting.Tests;

public sealed class HostTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "VisionPlatform-HostTests-" + Guid.NewGuid().ToString("N"));
    private static HostRequest Request(string operation, object? payload = null) => new() { RequestId = Guid.NewGuid().ToString("N"), Operation = operation, Payload = payload is null ? null : JsonDefaults.Element(payload) };
    private static T Data<T>(HostResponse response) { Assert.True(response.Success, response.ErrorCode + ": " + response.Message); return response.Payload!.Value.Deserialize<T>(JsonDefaults.Options)!; }
    private async Task<string> VisionFixture(NodeRegistry registry)
    {
        Directory.CreateDirectory(_folder);
        using (var builder = new ImageFrameBuilder(64, 32, PixelFormat.Mono8))
        { for (var y = 10; y < 20; y++) builder.GetRowSpan(y).Slice(20, 15).Fill(255); using var image = builder.Build(); ImageFile.Save(image, Path.Combine(_folder, "input.png")); }
        var project = new ProjectDocument { ProjectName = "Host real Blob inspection", Nodes = [
            Spec("input", "ImageInput", new ImageInputParameters { Path = "input.png" }), Spec("blob", "BlobDetection", new { MinArea = 1 }),
            Spec("decision", "ResultDecision", new ResultDecisionParameters { MinimumBlobCount = 1, MaximumBlobCount = 1 }), Spec("report", "ResultDisplay", new { }) ],
            Connections = [new("input", "Image", "blob", "Image"), new("blob", "BlobCount", "decision", "BlobCount"), new("decision", "Decision", "report", "Decision"), new("blob", "BlobCount", "report", "BlobCount")] };
        var path = Path.Combine(_folder, "vision.json"); await new ProjectStore(registry).SaveAsync(path, project); return path;
    }
    private static NodeSpecification Spec(string id, string type, object p) => new(id, type, id, JsonDefaults.Element(p));
    private async Task<string> DelayFixture(NodeRegistry registry, Signals signals, bool withImage = false)
    {
        registry.Register("HostTestDelay", () => new DelayNode(signals)); Directory.CreateDirectory(_folder);
        var nodes = new List<NodeSpecification> { Spec("wait", "HostTestDelay", new { }) }; var edges = new List<NodeConnection>();
        if (withImage)
        {
            using (var image = new ImageFrameBuilder(100, 100, PixelFormat.Mono8).Build()) ImageFile.Save(image, Path.Combine(_folder, "input.png"));
            nodes.Insert(0, Spec("input", "ImageInput", new ImageInputParameters { Path = "input.png" })); edges.Add(new("input", "Image", "wait", "Image"));
        }
        var path = Path.Combine(_folder, "delay.json"); await new ProjectStore(registry).SaveAsync(path, new() { Nodes = nodes, Connections = edges }); return path;
    }
    private static async Task<RuntimeHostClient> Connect(RuntimeHostServer server)
    { var client = new RuntimeHostClient(); await client.ConnectAsync("127.0.0.1", server.BoundPort); return client; }
    private static async Task<HostStatusSnapshot> WaitDone(RuntimeHostClient client, Guid id)
    {
        using var deadline = new CancellationTokenSource(5000);
        while (true) { var state = Data<HostStatusSnapshot>(await client.SendAsync(Request("status", new RunReference(id)), deadline.Token)); if (state.State != HostSessionState.Running) return state; await Task.Delay(10, deadline.Token); }
    }
    private static async Task Until(Func<bool> predicate)
    { using var ct = new CancellationTokenSource(5000); while (!predicate()) await Task.Delay(10, ct.Token); }

    [Fact]
    public async Task RealTcpRunsOldSchemaVisionProjectAndReturnsCurrentResultsWithoutNativeHandles()
    {
        var baseline = FrameHandle.ActiveBytes; var registry = BuiltinRegistration.CreateRegistry(); var path = await VisionFixture(registry);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server);
        var loaded = await client.SendAsync(Request("load", new LoadProjectCommand(path))); Assert.True(loaded.Success, loaded.Message); Assert.Equal(baseline, FrameHandle.ActiveBytes);
        var accepted = Data<HostRunAccepted>(await client.SendAsync(Request("run"))); Assert.Equal(HostSessionState.Completed, (await WaitDone(client, accepted.RunId)).State);
        var result = Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(accepted.RunId))));
        Assert.True(result.Success); Assert.NotEqual(accepted.RunId, result.EngineRunId); Assert.Equal(4, result.Nodes.Count);
        Assert.Equal(1, result.Nodes.Single(n => n.NodeId == "blob").Outputs["BlobCount"].Value.GetInt32());
        Assert.True(result.Nodes.Single(n => n.NodeId == "report").Outputs["Result"].Value.GetProperty("IsOk").GetBoolean());
        var json = JsonSerializer.Serialize(result, JsonDefaults.Options); Assert.DoesNotContain("BufferHandle", json); Assert.DoesNotContain("PixelRuns", json); Assert.DoesNotContain("DataPointer", json);
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
        using var reference = await new RuntimeExecutor(registry).ExecuteAsync((await new ProjectStore(registry).LoadAsync(path)).ToGraph(), _folder);
        Assert.Equal(reference.Nodes["blob"].Output<int>("BlobCount"), result.Nodes.Single(n => n.NodeId == "blob").Outputs["BlobCount"].Value.GetInt32());
        Assert.Equal(reference.Nodes["report"].Output<InspectionReport>("Result").IsOk, result.Nodes.Single(n => n.NodeId == "report").Outputs["Result"].Value.GetProperty("IsOk").GetBoolean());
    }
    [Fact]
    public async Task ExecuteToTargetAndRepeatedRunsNeverExposePreviousRunId()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var path = await VisionFixture(registry);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server);
        Assert.True((await client.SendAsync(Request("load", new LoadProjectCommand(path)))).Success);
        var first = Data<HostRunAccepted>(await client.SendAsync(Request("run", new RunProjectCommand("blob")))); await WaitDone(client, first.RunId);
        var firstResult = Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(first.RunId)))); Assert.Equal(new[] { "input", "blob" }, firstResult.Nodes.Select(n => n.NodeId));
        var second = Data<HostRunAccepted>(await client.SendAsync(Request("run"))); Assert.NotEqual(first.RunId, second.RunId); await WaitDone(client, second.RunId);
        Assert.Equal(HostErrorCodes.RunNotFound, (await client.SendAsync(Request("result", new RunReference(first.RunId)))).ErrorCode);
        Assert.Equal(HostErrorCodes.RunNotFound, (await client.SendAsync(Request("status"))).ErrorCode);
        var result = Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(second.RunId)))); Assert.Equal(4, result.Nodes.Count);
    }
    [Fact]
    public async Task LoadDoesNotExecuteAndAsyncBranchAllowsBusyStatusCancelAndIdempotentRunRetry()
    {
        var signals = new Signals(); var registry = BuiltinRegistration.CreateRegistry(); var path = await DelayFixture(registry, signals);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server);
        Assert.True((await client.SendAsync(Request("load", new LoadProjectCommand(path)))).Success); Assert.Equal(0, signals.Executions);
        var request = Request("run"); var first = Data<HostRunAccepted>(await client.SendAsync(request)); await signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var duplicate = Data<HostRunAccepted>(await client.SendAsync(request)); Assert.Equal(first.RunId, duplicate.RunId); Assert.Equal(1, signals.Executions);
        Assert.Equal(HostErrorCodes.InvalidRequest, (await client.SendAsync(request with { Payload = JsonDefaults.Element(new RunProjectCommand("wait")) })).ErrorCode);
        Assert.Equal(HostErrorCodes.Busy, (await client.SendAsync(Request("run"))).ErrorCode);
        Assert.Equal(HostErrorCodes.Busy, (await client.SendAsync(Request("load", new LoadProjectCommand(path)))).ErrorCode);
        Assert.Equal(HostErrorCodes.NotReady, (await client.SendAsync(Request("result", new RunReference(first.RunId)))).ErrorCode);
        var cancel = Data<HostStatusSnapshot>(await client.SendAsync(Request("cancel", new RunReference(first.RunId)))); Assert.True(cancel.CancellationRequested);
        Assert.Equal(HostSessionState.Cancelled, (await WaitDone(client, first.RunId)).State);
        Assert.Equal(NodeExecutionStatus.Cancelled, Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(first.RunId)))).Status);
        Assert.True((await client.SendAsync(Request("close"))).Success); await Until(() => server.ActiveConnections == 0); Assert.True(signals.Disposed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrServerStopCancelsActiveEngineAndReleasesImageAndServices(bool stopServer)
    {
        var baseline = FrameHandle.ActiveBytes; var registry = BuiltinRegistration.CreateRegistry(); var signals = new Signals(); var path = await DelayFixture(registry, signals, true);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); var client = await Connect(server);
        Assert.True((await client.SendAsync(Request("load", new LoadProjectCommand(path)))).Success); Data<HostRunAccepted>(await client.SendAsync(Request("run")));
        await signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(FrameHandle.ActiveBytes >= baseline + 10000);
        if (stopServer) await server.StopAsync(); else await client.DisposeAsync();
        await Until(() => server.ActiveConnections == 0); Assert.Equal(baseline, FrameHandle.ActiveBytes); Assert.True(signals.Disposed); await client.DisposeAsync();
    }
    [Fact]
    public async Task RunTimeoutIsCooperativeCancelledAndNotReplacedWithAStaleResult()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var signals = new Signals(); var path = await DelayFixture(registry, signals);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server);
        await client.SendAsync(Request("load", new LoadProjectCommand(path)));
        var run = Data<HostRunAccepted>(await client.SendAsync(Request("run", new RunProjectCommand(TimeoutMilliseconds: 75))));
        Assert.Equal(HostSessionState.Cancelled, (await WaitDone(client, run.RunId)).State); var result = Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(run.RunId))));
        Assert.False(result.Success); Assert.All(result.Nodes, n => Assert.Equal(NodeExecutionStatus.Cancelled, n.Status));
    }
    [Fact]
    public async Task FailingPluginCleanupStillCancelsEngineReleasesImagesAndClosesConnection()
    {
        var baseline = FrameHandle.ActiveBytes; var signals = new Signals { ThrowOnDispose = true }; var registry = BuiltinRegistration.CreateRegistry(); var path = await DelayFixture(registry, signals, true);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server); await client.SendAsync(Request("load", new LoadProjectCommand(path)));
        Data<HostRunAccepted>(await client.SendAsync(Request("run"))); await signals.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var closed = await client.SendAsync(Request("close")); Assert.False(closed.Success); Assert.Equal(HostErrorCodes.InternalError, closed.ErrorCode);
        await Until(() => server.ActiveConnections == 0); Assert.True(signals.Disposed); Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }
    [Fact]
    public async Task ClientSessionsCannotReadEachOthersResultsAndFailedLoadRetainsValidProjectIdentity()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var path = await VisionFixture(registry);
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var a = await Connect(server); await using var b = await Connect(server);
        await a.SendAsync(Request("load", new LoadProjectCommand(path))); await b.SendAsync(Request("load", new LoadProjectCommand(path)));
        var run = Data<HostRunAccepted>(await a.SendAsync(Request("run"))); await WaitDone(a, run.RunId);
        Assert.Equal(HostErrorCodes.RunNotFound, (await b.SendAsync(Request("result", new RunReference(run.RunId)))).ErrorCode);
        var bad = await a.SendAsync(Request("load", new LoadProjectCommand(Path.Combine(_folder, "missing.json")))); Assert.Equal(HostErrorCodes.ProjectInvalid, bad.ErrorCode);
        var state = Data<HostStatusSnapshot>(await a.SendAsync(Request("status", new RunReference(run.RunId)))); Assert.Equal("Host real Blob inspection", state.ProjectName);
    }
    [Fact]
    public async Task TcpFramingHandlesFragmentedAndCoalescedRequestsAndRejectsProtocolAndMalformedJson()
    {
        await using var server = new RuntimeHostServer(BuiltinRegistration.CreateRegistry()); await server.StartAsync(); using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, server.BoundPort); using var stream = raw.GetStream();
        var protocol = new FrameProtocolOptions { Mode = MessageBoundaryMode.LengthPrefix, MaximumMessageBytes = 1048576 }; var reader = new FramedMessageReader(stream, protocol);
        var invalidVersion = Request("status") with { ProtocolVersion = 2 };
        var bytes = FrameCodec.Encode(JsonSerializer.Serialize(invalidVersion, JsonDefaults.Options), protocol).Concat(FrameCodec.Encode(JsonSerializer.Serialize(Request("status"), JsonDefaults.Options), protocol)).ToArray();
        for (var i = 0; i < bytes.Length; i += 3) await stream.WriteAsync(bytes.AsMemory(i, Math.Min(3, bytes.Length - i)));
        using var deadline = new CancellationTokenSource(3000);
        var first = JsonSerializer.Deserialize<HostResponse>(await reader.ReadAsync(deadline.Token), JsonDefaults.Options)!; Assert.Equal(HostErrorCodes.UnsupportedVersion, first.ErrorCode);
        var second = JsonSerializer.Deserialize<HostResponse>(await reader.ReadAsync(deadline.Token), JsonDefaults.Options)!; Assert.Equal(HostSessionState.Empty, Data<HostStatusSnapshot>(second).State);
        await stream.WriteAsync(FrameCodec.Encode("{broken json", protocol)); var malformed = JsonSerializer.Deserialize<HostResponse>(await reader.ReadAsync(deadline.Token), JsonDefaults.Options)!; Assert.Equal(HostErrorCodes.InvalidRequest, malformed.ErrorCode);
    }
    [Fact]
    public async Task OversizedLengthPrefixIsRejectedBeforeAllocatingItsClaimedPayload()
    {
        await using var server = new RuntimeHostServer(BuiltinRegistration.CreateRegistry()); await server.StartAsync(); using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, server.BoundPort); using var stream = raw.GetStream();
        var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, uint.MaxValue); await stream.WriteAsync(prefix);
        var reader = new FramedMessageReader(stream, new() { Mode = MessageBoundaryMode.LengthPrefix, MaximumMessageBytes = 1048576 }); using var deadline = new CancellationTokenSource(3000);
        var response = JsonSerializer.Deserialize<HostResponse>(await reader.ReadAsync(deadline.Token), JsonDefaults.Options)!; Assert.Equal(HostErrorCodes.InvalidRequest, response.ErrorCode);
        await Until(() => server.ActiveConnections == 0);
    }
    [Fact]
    public async Task InvalidUtf8FrameReceivesStructuredErrorAndClosesItsSession()
    {
        await using var server = new RuntimeHostServer(BuiltinRegistration.CreateRegistry()); await server.StartAsync(); using var raw = new TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, server.BoundPort); using var stream = raw.GetStream();
        await stream.WriteAsync(new byte[] { 0, 0, 0, 2, 0xc3, 0x28 });
        var reader = new FramedMessageReader(stream, new() { Mode = MessageBoundaryMode.LengthPrefix, MaximumMessageBytes = 1048576 }); using var deadline = new CancellationTokenSource(3000);
        var response = JsonSerializer.Deserialize<HostResponse>(await reader.ReadAsync(deadline.Token), JsonDefaults.Options)!; Assert.Equal(HostErrorCodes.InvalidRequest, response.ErrorCode); Assert.Contains("UTF8", response.Message);
        await Until(() => server.ActiveConnections == 0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientTransportTimeoutOrCancellationClosesAnInterruptedConnection(bool cancel)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accept = listener.AcceptTcpClientAsync(); await using var client = new RuntimeHostClient(cancel ? 2000 : 100);
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port); using var remote = await accept; using var stream = remote.GetStream();
            using var cancellation = new CancellationTokenSource(); if (cancel) cancellation.CancelAfter(75);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Request("status"), cancellation.Token));
            else await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(Request("status")));
            using var deadline = new CancellationTokenSource(1000); var buffer = new byte[4096]; var total = 0; int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0) total += read;
            Assert.True(total > 0, "The test must have sent a real request before interrupting response reading.");
        }
        finally { listener.Stop(); }
    }
    [Fact]
    public async Task IdleConnectionTimeoutAndPortConflictsHaveFiniteLifecycles()
    {
        await using var server = new RuntimeHostServer(BuiltinRegistration.CreateRegistry(), new() { ReadTimeoutMilliseconds = 75 }); await server.StartAsync(); using var idle = new TcpClient(); await idle.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await Until(() => server.ActiveConnections == 1); await Until(() => server.ActiveConnections == 0);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); try
        { await using var conflict = new RuntimeHostServer(BuiltinRegistration.CreateRegistry(), new() { Port = ((IPEndPoint)listener.LocalEndpoint).Port }); await Assert.ThrowsAsync<SocketException>(() => conflict.StartAsync()); }
        finally { listener.Stop(); }
    }
    [Fact]
    public async Task ExplicitTrustedPluginAssemblyIsActuallyLoadedAndExecuted()
    {
        var registry = new NodeRegistry(); Assert.Equal(1, TrustedPluginLoader.Load(registry, typeof(FixtureEchoNode).Assembly.Location));
        Assert.Throws<ArgumentException>(() => TrustedPluginLoader.Load(registry, "plugin.dll"));
        Directory.CreateDirectory(_folder); var path = Path.Combine(_folder, "plugin.json"); await new ProjectStore(registry).SaveAsync(path, new() { Nodes = [Spec("plugin", "HostingFixtureEcho", new { })] });
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server); await client.SendAsync(Request("load", new LoadProjectCommand(path)));
        var run = Data<HostRunAccepted>(await client.SendAsync(Request("run"))); await WaitDone(client, run.RunId);
        Assert.Equal(42d, Data<HostResultSnapshot>(await client.SendAsync(Request("result", new RunReference(run.RunId)))).Nodes.Single().Outputs["Number"].Value.GetDouble());
    }
    [Fact]
    public async Task ProjectionBudgetReturnsClearErrorWithoutKeepingNativeOutputs()
    {
        var registry = new NodeRegistry(); registry.Register("text", () => new TextNode()); Directory.CreateDirectory(_folder); var path = Path.Combine(_folder, "many.json");
        await new ProjectStore(registry).SaveAsync(path, new() { Nodes = Enumerable.Range(0, 90).Select(i => Spec("n" + i, "text", new { })).ToList() });
        await using var server = new RuntimeHostServer(registry); await server.StartAsync(); await using var client = await Connect(server); await client.SendAsync(Request("load", new LoadProjectCommand(path)));
        var run = Data<HostRunAccepted>(await client.SendAsync(Request("run"))); Assert.Equal(HostSessionState.Completed, (await WaitDone(client, run.RunId)).State);
        Assert.Equal(HostErrorCodes.ResultTooLarge, (await client.SendAsync(Request("result", new RunReference(run.RunId)))).ErrorCode);
    }
    [Fact]
    public void UnknownPluginObjectsAreNotReflectedAndBlobPixelsAreNeverSerialized()
    {
        var unknown = HostResultProjector.ProjectValue(new Dangerous()); Assert.False(unknown.Value.GetProperty("Supported").GetBoolean());
        var blobs = new BlobResults([new(1, 150, 3, 4, new(1, 2, 3, 4)) { PixelRuns = new DangerousRuns() }]);
        var projected = HostResultProjector.ProjectValue(blobs); Assert.DoesNotContain("PixelRuns", projected.Value.GetRawText()); Assert.Equal(1, projected.Value.GetProperty("Count").GetInt32());
        var longText = HostResultProjector.ProjectValue(new string('x', 1000000)); Assert.True(longText.Truncated); Assert.Equal(16384, longText.Value.GetString()!.Length);
        Assert.False(HostResultProjector.ProjectValue(double.NaN).Value.GetProperty("Supported").GetBoolean());
    }
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.2")]
    public void NonLoopbackRequiresExplicitLanConfiguration(string ip)
    { Assert.Throws<ArgumentException>(() => new RuntimeHostOptions { ListenAddress = ip }.Validate()); new RuntimeHostOptions { ListenAddress = ip, AllowLan = true }.Validate(); }
    private sealed class Signals { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Executions; public bool Disposed; public bool ThrowOnDispose; }
    private sealed class TrackedResource(Signals signals) : IDisposable { public void Dispose() { signals.Disposed = true; if (signals.ThrowOnDispose) throw new IOException("Intentional cleanup regression fixture."); } }
    private sealed class DelayNode(Signals signals) : IExecutableNode
    {
        public NodeDescriptor Descriptor => new("HostTestDelay", "1.0.0", [new("Image", typeof(ImageFrame), false)], [new("Value", typeof(int))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken ct)
        { context.Services!.GetOrCreate("host.test.resource", () => new TrackedResource(signals)); Interlocked.Increment(ref signals.Executions); signals.Started.TrySetResult(); await Task.Delay(10000, ct); return new(new Dictionary<string, object> { ["Value"] = 1 }); }
    }
    private sealed class TextNode : IExecutableNode
    {
        public NodeDescriptor Descriptor => new("text", "1.0.0", [], [new("Text", typeof(string))]); public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct) => ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Text"] = new string('x', 16000) }));
    }
    private sealed class Dangerous { public string Throws => throw new Exception("Unknown plugin getter must never run."); public override string ToString() => throw new Exception("Unknown plugin ToString must never run."); }
    private sealed class DangerousRuns : IReadOnlyList<BlobPixelRun>
    { public int Count => int.MaxValue; public BlobPixelRun this[int i] => throw new Exception(); public IEnumerator<BlobPixelRun> GetEnumerator() => throw new Exception(); IEnumerator IEnumerable.GetEnumerator() => GetEnumerator(); }
    public void Dispose() { if (Directory.Exists(_folder) && Path.GetFileName(_folder).StartsWith("VisionPlatform-HostTests-", StringComparison.Ordinal)) Directory.Delete(_folder, true); }
}

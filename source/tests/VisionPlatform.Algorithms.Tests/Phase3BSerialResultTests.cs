using System.Globalization;
using System.IO.Ports;
using System.Text.Json;
using System.Threading.Channels;
using VisionPlatform.Algorithms.Results;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

/// <summary>Fake byte transports only. These tests do not open, scan or write any physical COM port.</summary>
public sealed class Phase3BSerialResultTests
{
    private static NodeSpecification Node<T>(string id, string type, T options) => new(id, type, type, JsonDefaults.Element(options));

    [Fact]
    public void FormatterUsesInvariantCsvAndCompactStructuredJson()
    {
        var report = new InspectionReport { InspectionId = Guid.NewGuid(), IsOk = true, BlobCount = 2, MatchScore = .9816,
            CenterX = 10.25, CenterY = -4.5, Angle = 23, Scale = 1.2, ExecutionMilliseconds = 8.25,
            Communication = new(CommunicationState.Sent, true, "No payload in status.") };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("OK,2,0.982", ResultFormatter.Format(report, new() { Format = ResultMessageFormat.Csv }));
            Assert.Equal("OK,2,0.982,10.250,-4.500,23.000,1.200,8.250,Sent", ResultFormatter.Format(report,
                new() { Format = ResultMessageFormat.Csv, IncludePoseInCsv = true, IncludeTimingInCsv = true, IncludeCommunicationInCsv = true }));
            var json = ResultFormatter.Format(report, new()); Assert.DoesNotContain('\n', json);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal("OK", parsed.RootElement.GetProperty("result").GetString());
            Assert.Equal(.982, parsed.RootElement.GetProperty("score").GetDouble());
            Assert.Equal(report.InspectionId, parsed.RootElement.GetProperty("inspectionId").GetGuid());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingMeasurementsStayNullAndInvalidNumbersFailClearly()
    {
        var report = new InspectionReport { IsOk = false };
        Assert.Equal("NG,,", ResultFormatter.Format(report, new() { Format = ResultMessageFormat.Csv }));
        using var json = JsonDocument.Parse(ResultFormatter.Format(report, new()));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("score").ValueKind);
        Assert.Throws<ArgumentException>(() => ResultFormatter.Format(report with { MatchScore = double.NaN }, new()));
        Assert.Throws<ArgumentException>(() => ResultFormatter.Format(report with { BlobCount = -1 }, new()));
        Assert.Throws<ArgumentException>(() => ResultFormatter.Format(report with { Scale = 0 }, new()));
        Assert.Throws<ArgumentException>(() => ResultFormatter.Format(report, new() { DecimalPlaces = 16 }));
        Assert.Throws<ArgumentException>(() => ResultFormatter.Format(report, new() { Format = (ResultMessageFormat)99 }));
    }

    [Fact]
    public async Task StructuredNodeUsesThisRunPoseTimingAndCommunicationWithoutCaching()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        var node = Node("result", "ResultDisplay", new ResultDisplayParameters { Options = new() { Format = ResultMessageFormat.Csv } });
        var match = new TemplateMatchResult(true, .982, 12, 18, 25, 1.1, new(12.5, 18.5, 10, 10, 25), TimeSpan.FromMilliseconds(7.25));
        using (var first = await executor.ExecuteSingleAsync(node, new Dictionary<string, object> { ["Decision"] = new InspectionDecision(true, "count in range"),
            ["BlobCount"] = 2, ["MatchResult"] = match, ["Communication"] = new CommunicationStatus(CommunicationState.Sent, true, "Sent") }))
        {
            var report = first.Nodes["result"].Output<InspectionReport>("Result");
            Assert.Equal(first.RunId, report.InspectionId); Assert.True(report.IsOk); Assert.Equal(2, report.BlobCount);
            Assert.Equal(12, report.CenterX); Assert.Equal(25, report.Angle); Assert.Equal(1.1, report.Scale);
            Assert.Equal(7.25, report.ExecutionMilliseconds); Assert.Equal(CommunicationState.Sent, report.Communication!.State);
            Assert.Equal("OK,2,0.982", first.Nodes["result"].Output<string>("Message"));
        }
        using var second = await executor.ExecuteSingleAsync(node, new Dictionary<string, object> { ["IsOk"] = false });
        var next = second.Nodes["result"].Output<InspectionReport>("Result");
        Assert.False(next.IsOk); Assert.Null(next.CenterX); Assert.Null(next.MatchScore); Assert.Null(next.Communication);
    }

    [Fact]
    public async Task ResultNodeRejectsContradictoryDecisionAndInvalidPoseAfterFailedMatch()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry()); var node = Node("r", "ResultDisplay", new ResultDisplayParameters());
        using var conflict = await executor.ExecuteSingleAsync(node, new Dictionary<string, object> { ["Decision"] = new InspectionDecision(true, "OK"), ["IsOk"] = false });
        Assert.Equal(NodeExecutionStatus.Failed, conflict.Status); Assert.Contains("disagree", conflict.ErrorMessage);
        using var missing = await executor.ExecuteSingleAsync(node, new Dictionary<string, object>());
        Assert.False(missing.Success); Assert.Contains("Decision or IsOk", missing.ErrorMessage);
        using var failedPose = await executor.ExecuteSingleAsync(node, new Dictionary<string, object> { ["IsOk"] = false,
            ["MatchResult"] = new TemplateMatchResult(false, 0, 0, 0, 0, 1, null, TimeSpan.Zero), ["Pose"] = new Pose2D(10, 20) });
        Assert.False(failedPose.Success); Assert.Contains("failed match", failedPose.ErrorMessage);
    }

    [Theory]
    [InlineData("PortName", "COM0")]
    [InlineData("PortName", "\\\\server\\COM1")]
    [InlineData("BaudRate", 0)]
    [InlineData("DataBits", 9)]
    [InlineData("Parity", 99)]
    [InlineData("StopBits", 0)]
    [InlineData("TimeoutMilliseconds", 0)]
    public void InvalidSerialOptionsAreRejectedWithoutOpeningHardware(string property, object value)
    {
        var json = JsonSerializer.SerializeToNode(new SerialOpenOptions(), JsonDefaults.Options)!.AsObject();
        json[property] = JsonSerializer.SerializeToNode(value);
        Assert.NotEmpty(new SerialOpenNode().ValidateParameters(JsonSerializer.SerializeToElement(json, JsonDefaults.Options)));
    }

    [Theory]
    [InlineData(MessageBoundaryMode.Delimiter)]
    [InlineData(MessageBoundaryMode.LengthPrefix)]
    public async Task FakeTransportFramesFragmentedUnicodeAndKeepsItsSettings(MessageBoundaryMode mode)
    {
        var fake = new FakeTransport(); await using var session = new SerialSession(() => fake);
        var options = new SerialOpenOptions { BaudRate = 115200, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.Two,
            Protocol = new() { Mode = mode } };
        var reference = await session.OpenAsync(options); Assert.Equal(options, fake.Options);
        var bytes = FrameCodec.Encode("测量,2", options.Protocol); fake.Input(bytes[..2]); fake.Input(bytes[2..]);
        Assert.Equal("测量,2", await session.ReceiveAsync(reference, 1000));
        var status = await session.SendAsync(reference, "send", "inspection-1", "OK,2,0.982", 1000);
        Assert.Equal(CommunicationState.Sent, status.State);
        Assert.Equal(FrameCodec.Encode("OK,2,0.982", options.Protocol), fake.Output);
        await session.CloseAsync(reference); Assert.Equal(0, session.OpenPortCount); Assert.False(fake.IsOpen);
    }

    [Fact]
    public async Task SendAttemptDeduplicationSurvivesReconnectAndForceResendIsExplicit()
    {
        var fakes = new List<FakeTransport>();
        await using var session = new SerialSession(() => { var next = new FakeTransport(); fakes.Add(next); return next; });
        var reference = await session.OpenAsync(new());
        Assert.Equal(CommunicationState.Sent, (await session.SendAsync(reference, "send", "part-1", "OK", 1000)).State);
        Assert.Equal(CommunicationState.DuplicateSuppressed, (await session.SendAsync(reference, "send", "part-1", "NG", 1000)).State);
        Assert.Equal(1, fakes[0].WriteCount);
        await session.CloseAsync(reference); reference = await session.OpenAsync(new());
        Assert.Equal(CommunicationState.DuplicateSuppressed, (await session.SendAsync(reference, "send", "part-1", "OK", 1000)).State);
        Assert.Equal(0, fakes[1].WriteCount);
        await session.SendAsync(reference, "send", "part-1", "OK", 1000, forceResend: true); Assert.Equal(1, fakes[1].WriteCount);
        Assert.DoesNotContain(session.Events, e => e.Description.Contains("part-1") || e.Description.Contains("OK"));
    }

    [Fact]
    public async Task SendLedgerIsBoundedWithoutEvictingEarlierKeysOrBypassingCapacity()
    {
        var fake = new FakeTransport(); await using var session = new SerialSession(() => fake, maximumSendAttempts: 2);
        var port = await session.OpenAsync(new());
        await session.SendAsync(port, "send", "first", "OK", 1000);
        await session.SendAsync(port, "send", "second", "NG", 1000);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(port, "send", "third", "OK", 1000));
        Assert.Contains("ledger is full", error.Message); Assert.Equal(2, fake.WriteCount);
        Assert.Equal(CommunicationState.DuplicateSuppressed, (await session.SendAsync(port, "send", "first", "OK", 1000)).State);
        await session.SendAsync(port, "send", "first", "OK", 1000, forceResend: true);
        Assert.Equal(3, fake.WriteCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(port, "send", "third", "OK", 1000, forceResend: true));
        Assert.Equal(3, fake.WriteCount); Assert.True(fake.IsOpen);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SerialSession(maximumSendAttempts: 0));
    }

    [Fact]
    public async Task SlowWriteUsesItsConfiguredDeadlineAndCancellationActivelyAbortsTransport()
    {
        var slow = new FakeTransport { WriteDelayMilliseconds = 150 };
        await using (var session = new SerialSession(() => slow))
        {
            var port = await session.OpenAsync(new());
            Assert.Equal(CommunicationState.Sent, (await session.SendAsync(port, "send", "slow", "OK", 1000)).State);
            Assert.True(slow.IsOpen); Assert.Equal(0, slow.AbortCount);
        }
        var blocking = new FakeTransport { BlockWriteUntilAbort = true }; await using var other = new SerialSession(() => blocking);
        var reference = await other.OpenAsync(new()); using var cancellation = new CancellationTokenSource();
        var send = other.SendAsync(reference, "send", "cancel", "OK", 5000, ct: cancellation.Token);
        await blocking.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(blocking.AbortCount > 0); Assert.False(blocking.IsOpen); Assert.Equal(0, blocking.ActiveWrites);
    }

    [Fact]
    public async Task CancellationObservesAbortFailureInsteadOfLeavingAnUnobservedCleanupTask()
    {
        var fake = new FakeTransport { BlockWriteUntilAbort = true, FailAbortAfterClosing = true };
        await using var session = new SerialSession(() => fake); var reference = await session.OpenAsync(new());
        using var cancellation = new CancellationTokenSource();
        var send = session.SendAsync(reference, "send", "cancel", "OK", 5000, ct: cancellation.Token);
        await fake.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        var error = await Assert.ThrowsAsync<IOException>(() => send.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("could not close", error.Message); Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(0, fake.ActiveWrites); Assert.False(fake.IsOpen);
    }

    [Fact]
    public async Task WaitingForAnExistingOpeningAttemptUsesItsOwnDeadline()
    {
        var fake = new FakeTransport { IgnoreOpenCancellationUntilReleased = true };
        await using var session = new SerialSession(() => fake);
        var options = new SerialOpenOptions { TimeoutMilliseconds = 40 };
        var first = session.OpenAsync(options); await fake.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => session.OpenAsync(options).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { fake.ReleaseOpen.TrySetResult(); }
        await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(fake.IsOpen); Assert.Equal(0, session.OpenPortCount);
    }

    [Fact]
    public async Task ReadTimeoutClosesPartialFrameAndCompletesThePendingRead()
    {
        var fake = new FakeTransport(); await using var session = new SerialSession(() => fake);
        var reference = await session.OpenAsync(new()); fake.Input([65, 66]);
        await Assert.ThrowsAsync<TimeoutException>(() => session.ReceiveAsync(reference, 30).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(fake.IsOpen); Assert.Equal(0, fake.ActiveReads); Assert.Equal(0, session.OpenPortCount);
        await Assert.ThrowsAsync<IOException>(() => session.ReceiveAsync(reference, 100));
    }

    [Fact]
    public async Task OpenTimeoutAndAccessFailureReleaseTheirTransports()
    {
        var blocked = new FakeTransport { BlockOpen = true }; await using var first = new SerialSession(() => blocked);
        await Assert.ThrowsAsync<TimeoutException>(() => first.OpenAsync(new() { TimeoutMilliseconds = 30 }).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(blocked.IsOpen); Assert.Equal(0, first.OpenPortCount);
        var denied = new FakeTransport { FailOpen = true }; await using var second = new SerialSession(() => denied);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => second.OpenAsync(new()));
        Assert.False(denied.IsOpen); Assert.Equal(0, second.OpenPortCount);
    }

    [Fact]
    public async Task OversizeReceiveClosesPortAndReconfigurationNeedsExplicitClose()
    {
        var fake = new FakeTransport(); await using var session = new SerialSession(() => fake);
        var options = new SerialOpenOptions { Protocol = new() { MaximumMessageBytes = 4 } };
        var reference = await session.OpenAsync(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync(options with { BaudRate = 19200 }));
        fake.Input([65, 66, 67, 68, 69, 70]);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ReceiveAsync(reference, 1000));
        Assert.False(fake.IsOpen); Assert.Equal(0, fake.ActiveReads);
    }

    [Fact]
    public async Task CancellationAndSessionCloseAbortPendingReads()
    {
        var fake = new FakeTransport(); await using var session = new SerialSession(() => fake);
        var reference = await session.OpenAsync(new()); using var cancel = new CancellationTokenSource();
        var read = session.ReceiveAsync(reference, 5000, cancel.Token); await fake.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read); Assert.False(fake.IsOpen); Assert.Equal(0, fake.ActiveReads);

        var second = new FakeTransport(); var other = new SerialSession(() => second);
        var another = await other.OpenAsync(new()); var pending = other.ReceiveAsync(another, 5000);
        await second.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); await other.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.False(second.IsOpen); Assert.Equal(0, second.ActiveReads);
    }

    [Fact]
    public async Task WriteFailureKeepsAttemptLedgerAndDoesNotRetryUncertainDelivery()
    {
        var first = new FakeTransport { FailWrite = true }; var second = new FakeTransport(); var creations = 0;
        await using var session = new SerialSession(() => creations++ == 0 ? first : second);
        var reference = await session.OpenAsync(new());
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync(reference, "send", "once", "NG", 1000));
        Assert.Equal(1, first.WriteCount); Assert.False(first.IsOpen);
        reference = await session.OpenAsync(new());
        Assert.Equal(CommunicationState.DuplicateSuppressed, (await session.SendAsync(reference, "send", "once", "NG", 1000)).State);
        Assert.Equal(0, second.WriteCount);
    }

    [Fact]
    public async Task SendTimeoutIsBoundedAndPortReferencesCannotCrossSessions()
    {
        var fake = new FakeTransport { BlockWrite = true }; await using var session = new SerialSession(() => fake);
        var reference = await session.OpenAsync(new());
        await Assert.ThrowsAsync<TimeoutException>(() => session.SendAsync(reference, "send", "key", "OK", 30).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(fake.IsOpen);
        await using var other = new SerialSession(() => new FakeTransport());
        await Assert.ThrowsAsync<ArgumentException>(() => other.ReceiveAsync(reference, 100));
    }

    [Fact]
    public async Task RuntimeUsesOnlyChosenSerialBranchAndSuppressesSameResultOnRerun()
    {
        await using var scope = new RuntimeExecutionSession(); var fakes = new List<FakeTransport>();
        scope.GetOrCreate(SerialSession.ServiceKey, () => new SerialSession(() => { var next = new FakeTransport(); fakes.Add(next); return next; }, scope.SessionId, scope.LifetimeCancellation));
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry(), logger: null, services: scope);
        NodeGraph Graph(bool condition) => new([
            Node("condition", "LogicNot", new { Value = !condition }), Node("if", "IfElse", new { }),
            Node("open-ok", "SerialOpen", new SerialOpenOptions { ConnectionId = "ok", PortName = "COM1" }),
            Node("open-ng", "SerialOpen", new SerialOpenOptions { ConnectionId = "ng", PortName = "COM2" }),
            Node("ok", "SerialSend", new SerialSendOptions { SendEnabled = true, DeliveryKey = "part", Message = "OK" }),
            Node("ng", "SerialSend", new SerialSendOptions { SendEnabled = true, DeliveryKey = "part", Message = "NG" })
        ], [new("condition", "Result", "if", "Condition"), new("if", "True", "open-ok", "$exec"), new("if", "False", "open-ng", "$exec"),
            new("open-ok", "Port", "ok", "Port"), new("open-ng", "Port", "ng", "Port")]);
        using (var first = await executor.ExecuteAsync(Graph(true)))
        {
            Assert.True(first.Success, first.ErrorMessage); Assert.Equal(NodeExecutionStatus.Skipped, first.Nodes["ng"].Status);
            Assert.Equal(NodeSkipReason.InactiveBranch, first.Nodes["ng"].SkipReason); Assert.Single(fakes); Assert.Equal(1, fakes[0].WriteCount);
        }
        using (var second = await executor.ExecuteAsync(Graph(false)))
        {
            Assert.True(second.Success, second.ErrorMessage); Assert.Empty(second.Nodes["ok"].Outputs); Assert.Equal(2, fakes.Count); Assert.Equal(1, fakes[1].WriteCount);
        }
        using var repeated = await executor.ExecuteAsync(Graph(true)); Assert.True(repeated.Success, repeated.ErrorMessage);
        Assert.Equal(CommunicationState.DuplicateSuppressed, repeated.Nodes["ok"].Output<CommunicationStatus>("Status").State);
        Assert.Equal(1, fakes[0].WriteCount);
    }

    [Fact]
    public async Task DisabledNodeDoesNotAcquireASerialServiceOrSendBytes()
    {
        var node = new SerialSendNode();
        var result = await node.ExecuteAsync(new(Node("send", "SerialSend", new SerialSendOptions()),
            new Dictionary<string, object> { ["Port"] = new SerialPortReference(Guid.NewGuid(), "missing") }, "."), default);
        Assert.Equal(CommunicationState.Disabled, ((CommunicationStatus)result.Values["Status"]).State);
    }

    private sealed class FakeTransport : ISerialTransport
    {
        private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource _closed = new();
        private readonly List<byte> _output = [];
        private byte[]? _pending; private int _offset;
        public bool IsOpen { get; private set; }
        public SerialOpenOptions? Options { get; private set; }
        public bool FailWrite { get; init; }
        public bool BlockWrite { get; init; }
        public bool BlockOpen { get; init; }
        public bool FailOpen { get; init; }
        public bool IgnoreOpenCancellationUntilReleased { get; init; }
        public bool BlockWriteUntilAbort { get; init; }
        public bool FailAbortAfterClosing { get; init; }
        public int WriteDelayMilliseconds { get; init; }
        public int WriteCount { get; private set; }
        public int AbortCount;
        public int ActiveReads;
        public int ActiveWrites;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOpen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Output => _output.ToArray();
        public Stream Stream { get; }
        public FakeTransport() => Stream = new FakeStream(this);
        public async ValueTask OpenAsync(SerialOpenOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Options = options; OpenStarted.TrySetResult();
            if (IgnoreOpenCancellationUntilReleased) await ReleaseOpen.Task;
            ct.ThrowIfCancellationRequested();
            if (BlockOpen) await Task.Delay(Timeout.Infinite, ct);
            if (FailOpen) throw new UnauthorizedAccessException("Simulated occupied COM port.");
            IsOpen = true;
        }
        public void Input(byte[] bytes) => _input.Writer.TryWrite(bytes);
        public async ValueTask AbortAsync()
        {
            Interlocked.Increment(ref AbortCount); IsOpen = false; _closed.Cancel(); _input.Writer.TryComplete();
            if (FailAbortAfterClosing) { await Task.Yield(); throw new IOException("Simulated abort cleanup failure."); }
        }
        public ValueTask DisposeAsync() => AbortAsync();
        private sealed class FakeStream(FakeTransport owner) : Stream
        {
            public override bool CanRead => owner.IsOpen; public override bool CanWrite => owner.IsOpen; public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                Interlocked.Increment(ref owner.ActiveReads); owner.ReadStarted.TrySetResult();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, owner._closed.Token);
                try
                {
                    if (owner._pending is null || owner._offset == owner._pending.Length) { owner._pending = await owner._input.Reader.ReadAsync(linked.Token); owner._offset = 0; }
                    var count = Math.Min(buffer.Length, owner._pending.Length - owner._offset);
                    owner._pending.AsMemory(owner._offset, count).CopyTo(buffer); owner._offset += count; return count;
                }
                finally { Interlocked.Decrement(ref owner.ActiveReads); }
            }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested(); owner.WriteCount++; Interlocked.Increment(ref owner.ActiveWrites); owner.WriteStarted.TrySetResult();
                try
                {
                    if (owner.WriteDelayMilliseconds > 0) await Task.Delay(owner.WriteDelayMilliseconds, ct);
                    if (owner.BlockWrite) await Task.Delay(Timeout.Infinite, ct);
                    if (owner.BlockWriteUntilAbort) await Task.Delay(Timeout.Infinite, owner._closed.Token);
                    if (owner.FailWrite) throw new IOException("Simulated incomplete write.");
                    owner._output.AddRange(buffer.ToArray());
                }
                finally { Interlocked.Decrement(ref owner.ActiveWrites); }
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}

using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class RuntimeExecutionSessionTests
{
    [Fact]
    public async Task ConcurrentFactoriesCreateOnceAndAsyncCleanupRunsInReverseOrder()
    {
        var order = new List<string>(); var created = 0; var session = new RuntimeExecutionSession();
        var first = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => session.GetOrCreate("first", () =>
        { Interlocked.Increment(ref created); return new Probe(() => order.Add("first")); }))));
        Assert.Equal(1, created); Assert.All(first, instance => Assert.Same(first[0], instance));
        session.GetOrCreate("second", () => new AsyncProbe(() => order.Add("second")));
        var lifetime = session.LifetimeCancellation;
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.Equal(["second", "first"], order); Assert.True(lifetime.IsCancellationRequested);
        Assert.False(session.TryGet<Probe>("first", out _));
        Assert.Throws<ObjectDisposedException>(() => session.GetOrCreate("new", () => new object()));
    }

    [Fact]
    public async Task CleanupFailureDoesNotPreventOtherServicesClosing()
    {
        var order = new List<string>(); var session = new RuntimeExecutionSession();
        session.GetOrCreate("first", () => new Probe(() => order.Add("first")));
        session.GetOrCreate("second", () => new Probe(() => { order.Add("second"); throw new IOException("close failed"); }));
        session.GetOrCreate("third", () => new AsyncProbe(() => order.Add("third")));
        var error = await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask());
        Assert.Contains("close failed", error.ToString()); Assert.Equal(["third", "second", "first"], order);
    }

    [Fact]
    public async Task RemovalClosesAServiceAndAllowsExplicitRecreation()
    {
        var count = 0; await using var session = new RuntimeExecutionSession();
        var first = session.GetOrCreate("connection", () => new Probe(() => count++));
        Assert.True(session.TryGet<Probe>("connection", out var borrowed)); Assert.Same(first, borrowed);
        await session.RemoveAsync("connection"); Assert.Equal(1, count); Assert.False(session.TryGet<Probe>("connection", out _));
        var second = session.GetOrCreate("connection", () => new Probe(() => count++)); Assert.NotSame(first, second);
        await session.DisposeAsync(); Assert.Equal(2, count);
    }

    [Fact]
    public async Task DuplicateReferencesAreOwnedOnlyOnceAndFailedFactoryIsNotRetained()
    {
        var count = 0; await using var session = new RuntimeExecutionSession(); var service = new Probe(() => count++);
        Assert.Throws<InvalidOperationException>(() => session.GetOrCreate<object>("bad", () => throw new InvalidOperationException("factory")));
        session.GetOrCreate("a", () => service); session.GetOrCreate("b", () => service);
        await session.RemoveAsync("a"); Assert.Equal(0, count);
        await session.DisposeAsync(); Assert.Equal(1, count);
    }

    [Fact]
    public async Task StandaloneRuntimeClosesServicesBeforeReturningButRetainsOutputResources()
    {
        var serviceClosed = 0; var outputClosed = 0; Guid actualRunId = default; var registry = new NodeRegistry();
        registry.Register("probe", () => new ServiceNode(context =>
        {
            actualRunId = context.RunId;
            context.Services!.GetOrCreate("service", () => new Probe(() => serviceClosed++));
            var output = new Probe(() => outputClosed++);
            return new(new Dictionary<string, object> { ["Resource"] = output }, [output]);
        }));
        var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(Spec(), new Dictionary<string, object>());
        Assert.True(run.Success); Assert.Equal(1, serviceClosed); Assert.Equal(0, outputClosed); Assert.Equal(run.RunId, actualRunId);
        run.Dispose(); Assert.Equal(1, outputClosed);
    }

    [Fact]
    public async Task InjectedProjectScopePersistsAcrossRunsUntilHostClosesIt()
    {
        var closed = 0; var instances = new List<object>(); var registry = new NodeRegistry();
        registry.Register("probe", () => new ServiceNode(context =>
        {
            instances.Add(context.Services!.GetOrCreate("connection", () => new Probe(() => closed++)));
            return new(new Dictionary<string, object> { ["Resource"] = new object() });
        }));
        await using var session = new RuntimeExecutionSession(); var executor = new RuntimeExecutor(registry, logger: null, services: session);
        using var first = await executor.ExecuteSingleAsync(Spec(), new Dictionary<string, object>());
        using var second = await executor.ExecuteSingleAsync(Spec(), new Dictionary<string, object>());
        Assert.Equal(0, closed); Assert.Same(instances[0], instances[1]);
        await session.DisposeAsync(); Assert.Equal(1, closed);
    }

    [Fact]
    public async Task CleanupExceptionReturnsInspectableFailureAndStillDisposesAllServices()
    {
        var closed = 0; var registry = new NodeRegistry();
        registry.Register("probe", () => new ServiceNode(context =>
        {
            context.Services!.GetOrCreate("healthy", () => new Probe(() => closed++));
            context.Services.GetOrCreate("broken", () => new Probe(() => throw new IOException("service close error")));
            return new(new Dictionary<string, object> { ["Resource"] = new object() });
        }));
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(new([Spec()], []));
        Assert.False(run.Success); Assert.Contains("Session cleanup failed", run.ErrorMessage); Assert.Equal(1, closed);
        Assert.True(run.Nodes["probe"].Outputs.ContainsKey("Resource"));
    }

    [Fact]
    public void SynchronousFallbackDoesNotCaptureTheBlockedHostSynchronizationContext()
    {
        Exception? failure = null; var closed = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingContext());
            try
            {
                var session = new RuntimeExecutionSession();
                session.GetOrCreate("async", () => new AsyncProbe(() => closed = true));
                session.Dispose();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(3)), "Synchronous disposal captured the blocked host context.");
        Assert.Null(failure); Assert.True(closed);
    }

    [Fact]
    public async Task ClosingInjectedScopeCancelsCurrentNodeAndCannotStartDownstreamWork()
    {
        var registry = new NodeRegistry(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register("waiting", () => new WaitingNode(entered));
        await using var session = new RuntimeExecutionSession();
        var execution = new RuntimeExecutor(registry, logger: null, services: session).ExecuteSingleAsync(
            new("waiting", "waiting", "waiting", JsonDefaults.Element(new { })), new Dictionary<string, object>());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); await session.DisposeAsync();
        using var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NodeExecutionStatus.Cancelled, result.Status); Assert.Empty(result.Nodes["waiting"].Outputs);
    }

    private static NodeSpecification Spec() => new("probe", "probe", "probe", JsonDefaults.Element(new { }));
    private sealed class ServiceNode(Func<NodeExecutionContext, NodeOutput> execute) : IExecutableNode
    {
        public NodeDescriptor Descriptor => new("probe", "1.0.0", [], [new("Resource", typeof(object))]);
        public IReadOnlyList<string> ValidateParameters(System.Text.Json.JsonElement parameters) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(execute(context));
    }
    private sealed class Probe(Action close) : IDisposable { public void Dispose() => close(); }
    private sealed class AsyncProbe(Action close) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Task.Yield(); close(); }
    }
    private sealed class NonPumpingContext : SynchronizationContext
    { public override void Post(SendOrPostCallback callback, object? state) { } }
    private sealed class WaitingNode(TaskCompletionSource entered) : IExecutableNode
    {
        public NodeDescriptor Descriptor => new("waiting", "1.0.0", [], []);
        public IReadOnlyList<string> ValidateParameters(System.Text.Json.JsonElement parameters) => [];
        public async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(new Dictionary<string, object>());
        }
    }
}

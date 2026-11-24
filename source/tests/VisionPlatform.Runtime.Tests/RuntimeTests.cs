using System.Text.Json;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class RuntimeTests
{
    internal static NodeSpecification Spec(string id, string type, object? parameters = null) => new(id, type, id, JsonDefaults.Element(parameters ?? new { }));
    internal static NodeConnection Edge(string from, string to, string input = "Value") => new(from, "Value", to, input);
    private static NodeRegistry Registry()
    {
        var registry = new NodeRegistry();
        registry.Register("source", () => new TestNode("source", [], [new("Value", typeof(int))], c => new(new Dictionary<string, object> { ["Value"] = c.Parameters<SourceParameters>().Value })));
        registry.Register("double", () => new TestNode("double", [new("Value", typeof(int))], [new("Value", typeof(int))], c => new(new Dictionary<string, object> { ["Value"] = c.Input<int>("Value") * 2 })));
        registry.Register("sum", () => new TestNode("sum", [new("A", typeof(int)), new("B", typeof(int))], [new("Value", typeof(int))], c => new(new Dictionary<string, object> { ["Value"] = c.Input<int>("A") + c.Input<int>("B") })));
        return registry;
    }
    private static NodeGraph BranchGraph() => new([
        Spec("a", "source", new { Value = 3 }), Spec("b", "double"), Spec("c", "double"), Spec("d", "sum")
    ], [Edge("a", "b"), Edge("a", "c"), Edge("b", "d", "A"), Edge("c", "d", "B")]);

    [Fact]
    public async Task DagBranchesAndMultipleInputsProduceInspectableDeterministicOutputs()
    {
        var executor = new RuntimeExecutor(Registry());
        using var first = await executor.ExecuteAsync(BranchGraph());
        using var second = await executor.ExecuteAsync(BranchGraph());
        Assert.True(first.Success);
        Assert.Equal(12, first.Nodes["d"].Output<int>("Value"));
        Assert.Equal(6, first.Nodes["b"].Output<int>("Value"));
        Assert.Equal(first.Nodes["d"].Output<int>("Value"), second.Nodes["d"].Output<int>("Value"));
        Assert.All(first.Nodes.Values, n => Assert.True(n.ExecutionTime >= TimeSpan.Zero));
        Assert.NotEqual(first.RunId, second.RunId);
    }

    [Fact]
    public async Task ExecuteToRunsOnlyTargetAncestors()
    {
        using var run = await new RuntimeExecutor(Registry()).ExecuteToAsync(BranchGraph(), "b");
        Assert.Equal(new[] { "a", "b" }, run.Nodes.Keys.ToArray());
    }

    [Fact]
    public async Task SingleNodeRequiresExplicitTypedSnapshotAndDoesNotUsePriorRun()
    {
        var executor = new RuntimeExecutor(Registry());
        using var run = await executor.ExecuteSingleAsync(Spec("b", "double"), new Dictionary<string, object> { ["Value"] = 7 });
        Assert.Equal(14, run.Nodes["b"].Output<int>("Value"));
        await Assert.ThrowsAsync<GraphValidationException>(() => executor.ExecuteSingleAsync(Spec("b", "double"), new Dictionary<string, object>()));
        await Assert.ThrowsAsync<GraphValidationException>(() => executor.ExecuteSingleAsync(Spec("b", "double"), new Dictionary<string, object> { ["Value"] = "7" }));
    }

    [Fact]
    public async Task GraphSnapshotDetachesJsonLifetime()
    {
        NodeGraph graph;
        using (var doc = JsonDocument.Parse("{\"Value\":8}"))
            graph = new NodeGraph([new("a", "source", "a", doc.RootElement)], []);
        using var run = await new RuntimeExecutor(Registry()).ExecuteAsync(graph);
        Assert.Equal(8, run.Nodes["a"].Output<int>("Value"));
    }

    [Fact]
    public void TypeMismatchIsRejectedBeforeExecution()
    {
        var registry = Registry();
        registry.Register("text", () => new TestNode("text", [new("Value", typeof(string))], [], _ => new(new Dictionary<string, object>())));
        var errors = new GraphValidator(registry).Validate(new([Spec("a", "source"), Spec("b", "text")], [Edge("a", "b")]));
        Assert.Contains(errors, e => e.Contains("type mismatch"));
    }

    [Fact]
    public void MissingPortsDuplicateConnectionsMissingTypesAndCyclesAreRejected()
    {
        var validator = new GraphValidator(Registry());
        Assert.Contains(validator.Validate(new([Spec("a", "double")], [])), e => e.Contains("not connected"));
        Assert.Contains(validator.Validate(new([Spec("a", "source"), Spec("b", "double")], [new("a", "Bad", "b", "Value")])), e => e.Contains("Unknown output"));
        Assert.Contains(validator.Validate(new([Spec("a", "source"), Spec("b", "double")], [Edge("a", "b"), Edge("a", "b")])), e => e.Contains("multiple sources"));
        Assert.Contains(validator.Validate(new([Spec("a", "double"), Spec("b", "double")], [Edge("a", "b"), Edge("b", "a")])), e => e.Contains("cycle"));
        Assert.Contains(validator.Validate(new([Spec("a", "not-installed")], [])), e => e.Contains("not installed"));
        Assert.Contains(validator.Validate(new([Spec("a", "source"), Spec("a", "source")], [])), e => e.Contains("Duplicate node"));
        Assert.Contains(validator.Validate(new([], [])), e => e.Contains("at least one"));
    }

    [Fact]
    public void VersionMismatchIsRejected()
    {
        var errors = new GraphValidator(Registry()).Validate(new([Spec("a", "source") with { PluginVersion = "2.0.0" }], []));
        Assert.Contains(errors, e => e.Contains("version"));
    }

    [Fact]
    public async Task InvalidParametersAreNodeFailureAndIndependentBranchContinues()
    {
        var registry = Registry();
        registry.Register("invalid", () => new TestNode("invalid", [], [new("Value", typeof(int))], _ => throw new Exception("must not execute"), ["KernelSize must be a positive odd integer."]));
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(new([
            Spec("bad", "invalid"), Spec("child", "double"), Spec("independent", "source", new { Value = 5 })
        ], [Edge("bad", "child")]));
        Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["bad"].Status);
        Assert.Contains("KernelSize", run.Nodes["bad"].ErrorMessage);
        Assert.Equal(NodeExecutionStatus.Skipped, run.Nodes["child"].Status);
        Assert.Equal(5, run.Nodes["independent"].Output<int>("Value"));
    }

    [Fact]
    public async Task ThrownExceptionBecomesErrorAndNoOldOutputIsUsed()
    {
        var registry = Registry();
        registry.Register("throw", () => new TestNode("throw", [], [new("Value", typeof(int))], _ => throw new InvalidOperationException("camera disconnected")));
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(new([Spec("bad", "throw"), Spec("child", "double")], [Edge("bad", "child")]));
        Assert.False(run.Success);
        Assert.Contains("camera disconnected", run.Nodes["bad"].ErrorMessage);
        Assert.Empty(run.Nodes["bad"].Outputs);
        Assert.Empty(run.Nodes["child"].Outputs);
    }

    [Fact]
    public async Task MissingOptionalPoseSkipsDownstreamWithoutUsingPriorRun()
    {
        var registry = Registry();
        registry.Register("matcher", () => new TestNode("matcher", [], [new("Value", typeof(int), false)], c =>
            c.Parameters<MatchParameters>().Success ? new(new Dictionary<string, object> { ["Value"] = 42 }) : new(new Dictionary<string, object>())));
        var executor = new RuntimeExecutor(registry);
        using var good = await executor.ExecuteAsync(new([Spec("match", "matcher", new { Success = true }), Spec("roi", "double")], [Edge("match", "roi")]));
        Assert.Equal(84, good.Nodes["roi"].Output<int>("Value"));
        using var bad = await executor.ExecuteAsync(new([Spec("match", "matcher", new { Success = false }), Spec("roi", "double")], [Edge("match", "roi")]));
        Assert.Equal(NodeExecutionStatus.Success, bad.Nodes["match"].Status);
        Assert.Equal(NodeExecutionStatus.Skipped, bad.Nodes["roi"].Status);
        Assert.Contains("unavailable", bad.Nodes["roi"].ErrorMessage);
        Assert.Empty(bad.Nodes["roi"].Outputs);
    }

    [Fact]
    public async Task PreCancellationRunsNoNodes()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var run = await new RuntimeExecutor(Registry()).ExecuteAsync(BranchGraph(), cancellationToken: cancellation.Token);
        Assert.Equal(NodeExecutionStatus.Cancelled, run.Status);
        Assert.All(run.Nodes.Values, n => Assert.Equal(NodeExecutionStatus.Cancelled, n.Status));
    }

    [Fact]
    public async Task InFlightCancellationIsCooperativeAndReported()
    {
        var registry = new NodeRegistry();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register("wait", () => new AsyncTestNode("wait", async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(new Dictionary<string, object>()); }));
        using var cancellation = new CancellationTokenSource();
        var task = new RuntimeExecutor(registry).ExecuteAsync(new([Spec("wait", "wait")], []), cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        using var run = await task;
        Assert.Equal(NodeExecutionStatus.Cancelled, run.Nodes["wait"].Status);
    }

    [Fact]
    public async Task OutputResourcesSurviveForInspectionAndAreReleasedExactlyOnce()
    {
        var resource = new DisposalProbe();
        var registry = new NodeRegistry();
        registry.Register("owned", () => new TestNode("owned", [], [], _ => new(new Dictionary<string, object>(), [resource, resource])));
        var run = await new RuntimeExecutor(registry).ExecuteAsync(new([Spec("one", "owned"), Spec("two", "owned")], []));
        Assert.Equal(0, resource.DisposeCount);
        run.Dispose(); run.Dispose();
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task InvalidOutputBecomesFailureAndResourceStillGetsReleased()
    {
        var resource = new DisposalProbe();
        var registry = new NodeRegistry();
        registry.Register("bad-output", () => new TestNode("bad-output", [], [new("Value", typeof(int))], _ => new(new Dictionary<string, object> { ["Value"] = "wrong" }, [resource])));
        var run = await new RuntimeExecutor(registry).ExecuteAsync(new([Spec("a", "bad-output")], []));
        Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["a"].Status);
        Assert.Contains("type", run.Nodes["a"].ErrorMessage);
        run.Dispose();
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task BorrowedSnapshotCannotBeOwnedByNode()
    {
        var resource = new DisposalProbe();
        var registry = new NodeRegistry();
        registry.Register("borrow", () => new TestNode("borrow", [new("Resource", typeof(DisposalProbe))], [], c => new(new Dictionary<string, object>(), [c.Input<DisposalProbe>("Resource")])));
        using (var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(Spec("a", "borrow"), new Dictionary<string, object> { ["Resource"] = resource }))
        {
            Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["a"].Status);
            Assert.Contains("borrowed input", run.Nodes["a"].ErrorMessage);
        }
        Assert.Equal(0, resource.DisposeCount);
        resource.Dispose();
    }

    [Fact]
    public void RegistryDiscoversPublicParameterlessPluginsAndRejectsDuplicateRegistration()
    {
        var registry = new NodeRegistry();
        Assert.Equal(1, registry.Discover(typeof(DiscoverableNode).Assembly));
        Assert.IsType<DiscoverableNode>(registry.Create("discovered"));
        Assert.Throws<ArgumentException>(() => registry.Register("discovered", () => new DiscoverableNode()));
        Assert.Throws<ArgumentException>(() => registry.Register("other", () => new DiscoverableNode()));
    }

    private sealed record SourceParameters(int Value = 1);
    private sealed record MatchParameters(bool Success);
    private sealed class DisposalProbe : IDisposable { public int DisposeCount; public void Dispose() => DisposeCount++; }
    private sealed class TestNode(string type, IReadOnlyList<PortDefinition> inputs, IReadOnlyList<PortDefinition> outputs,
        Func<NodeExecutionContext, NodeOutput> execute, IReadOnlyList<string>? validationErrors = null) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new(type, "1.0.0", inputs, outputs);
        public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => validationErrors ?? Array.Empty<string>();
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(execute(context));
    }
    private sealed class AsyncTestNode(string type, Func<CancellationToken, Task<NodeOutput>> execute) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new(type, "1.0.0", [], []);
        public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => [];
        public async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => await execute(cancellationToken);
    }
}

public sealed class DiscoverableNode : IExecutableNode
{
    public NodeDescriptor Descriptor { get; } = new("discovered", "1.0.0", [], []);
    public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => [];
    public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>()));
}

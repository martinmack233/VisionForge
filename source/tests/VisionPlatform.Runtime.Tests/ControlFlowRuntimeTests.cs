using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class ControlFlowRuntimeTests
{
    private static NodeSpecification Spec(string id, string type, object? parameters = null) => new(id, type, id, JsonDefaults.Element(parameters ?? new { }));
    private static NodeConnection Edge(string from, string port, string to, string input) => new(from, port, to, input);
    private static NodeRegistry Registry(List<string> effects)
    {
        var registry = new NodeRegistry();
        registry.Register("condition", () => new TestNode("condition", [], [new("Condition", typeof(bool))], c => new(new Dictionary<string, object> { ["Condition"] = c.Parameters<ConditionParameters>().Value })));
        registry.Register("if", () => new TestNode("if", [new("Condition", typeof(bool))], [NodePorts.Control("True"), NodePorts.Control("False")],
            c => new(new Dictionary<string, object> { [c.Input<bool>("Condition") ? "True" : "False"] = ControlSignal.Active })));
        registry.Register("effect", () => new TestNode("effect", [], [new("Value", typeof(string))], c =>
        { effects.Add(c.Spec.NodeId); return new(new Dictionary<string, object> { ["Value"] = c.Spec.NodeId }); }));
        registry.Register("consume", () => new TestNode("consume", [new("Value", typeof(string))], [new("Value", typeof(string))], c =>
        { effects.Add(c.Spec.NodeId); return new(new Dictionary<string, object> { ["Value"] = c.Input<string>("Value") }); }));
        registry.Register("merge", () => new TestMerge());
        return registry;
    }
    private static NodeGraph BranchGraph(bool condition) => new([
        Spec("condition", "condition", new { Value = condition }), Spec("if", "if"), Spec("true", "effect"), Spec("false", "effect"), Spec("merge", "merge"), Spec("after", "consume")
    ], [Edge("condition", "Condition", "if", "Condition"), Edge("if", "True", "true", NodePorts.Execute), Edge("if", "False", "false", NodePorts.Execute),
        Edge("true", NodePorts.Completed, "merge", "TrueControl"), Edge("false", NodePorts.Completed, "merge", "FalseControl"),
        Edge("true", "Value", "merge", "TrueValue"), Edge("false", "Value", "merge", "FalseValue"), Edge("merge", "Value", "after", "Value")]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BranchSkipsRealSideEffectAndMergeUsesOnlyCurrentSelectedValue(bool condition)
    {
        var effects = new List<string>(); var selected = condition ? "true" : "false"; var inactive = condition ? "false" : "true";
        using var run = await new RuntimeExecutor(Registry(effects)).ExecuteAsync(BranchGraph(condition));
        Assert.True(run.Success, run.ErrorMessage); Assert.Null(run.ErrorMessage);
        Assert.Equal([selected, "after"], effects);
        Assert.Equal(NodeExecutionStatus.Skipped, run.Nodes[inactive].Status); Assert.Equal(NodeSkipReason.InactiveBranch, run.Nodes[inactive].SkipReason);
        Assert.Empty(run.Nodes[inactive].Outputs); Assert.Equal(selected, run.Nodes["merge"].Output<string>("Value"));
        Assert.DoesNotContain(NodePorts.Completed, run.Nodes[selected].Outputs.Keys);
    }

    [Fact]
    public async Task RepeatedOppositeBranchesNeverUsePreviousValue()
    {
        var effects = new List<string>(); var executor = new RuntimeExecutor(Registry(effects));
        using var first = await executor.ExecuteAsync(BranchGraph(true));
        using var second = await executor.ExecuteAsync(BranchGraph(false));
        Assert.Equal("true", first.Nodes["merge"].Output<string>("Value")); Assert.Equal("false", second.Nodes["merge"].Output<string>("Value"));
        Assert.Empty(second.Nodes["true"].Outputs); Assert.Equal(["true", "after", "false", "after"], effects);
    }

    [Fact]
    public async Task InactiveGatePrecedesParameterValidationAndAbsentData()
    {
        var effects = new List<string>(); var registry = Registry(effects);
        registry.Register("invalid", () => new TestNode("invalid", [new("Missing", typeof(string))], [], _ => throw new Exception("must never invoke"), ["invalid parameter"]));
        var graph = new NodeGraph([Spec("condition", "condition", new { Value = false }), Spec("if", "if"), Spec("bad", "invalid"), Spec("source", "effect")],
            [Edge("condition", "Condition", "if", "Condition"), Edge("if", "True", "bad", NodePorts.Execute), Edge("source", "Value", "bad", "Missing")]);
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(graph);
        Assert.True(run.Success); Assert.Equal(NodeSkipReason.InactiveBranch, run.Nodes["bad"].SkipReason);
        using var single = await new RuntimeExecutor(registry).ExecuteSingleAsync(Spec("bad", "invalid"), new Dictionary<string, object> { [NodePorts.Execute] = ControlSignal.Inactive });
        Assert.True(single.Success); Assert.Equal(NodeSkipReason.InactiveBranch, single.Nodes["bad"].SkipReason);
    }

    [Fact]
    public async Task InactiveDataDependencyPropagatesWithoutInvokingConsumer()
    {
        var effects = new List<string>(); var graph = BranchGraph(false);
        var extended = new NodeGraph(graph.Nodes.Append(Spec("inactiveChild", "consume")), graph.Connections.Append(Edge("true", "Value", "inactiveChild", "Value")));
        using var run = await new RuntimeExecutor(Registry(effects)).ExecuteAsync(extended);
        Assert.True(run.Success); Assert.DoesNotContain("inactiveChild", effects); Assert.Equal(NodeSkipReason.InactiveBranch, run.Nodes["inactiveChild"].SkipReason);
    }

    [Fact]
    public async Task NestedInactiveConditionCannotTriggerEitherSubbranch()
    {
        var effects = new List<string>(); var graph = BranchGraph(false);
        var nodes = graph.Nodes.Concat([Spec("nestedIf", "if"), Spec("nestedTrue", "effect"), Spec("nestedFalse", "effect")]);
        var edges = graph.Connections.Concat([Edge("condition", "Condition", "nestedIf", "Condition"), Edge("if", "True", "nestedIf", NodePorts.Execute),
            Edge("nestedIf", "True", "nestedTrue", NodePorts.Execute), Edge("nestedIf", "False", "nestedFalse", NodePorts.Execute)]);
        using var run = await new RuntimeExecutor(Registry(effects)).ExecuteAsync(new(nodes, edges));
        Assert.True(run.Success); Assert.DoesNotContain("nestedTrue", effects); Assert.DoesNotContain("nestedFalse", effects);
        Assert.Equal(NodeSkipReason.InactiveBranch, run.Nodes["nestedIf"].SkipReason);
        Assert.Equal(NodeSkipReason.InactiveBranch, run.Nodes["nestedFalse"].SkipReason);
    }

    [Fact]
    public async Task SelectedBranchFailureCannotBeMergedOrHiddenAsNormalSkip()
    {
        var effects = new List<string>(); var registry = Registry(effects);
        registry.Register("fail", () => new TestNode("fail", [], [new("Value", typeof(string))], _ => throw new InvalidOperationException("transport unavailable")));
        var graph = BranchGraph(true); var failing = new NodeGraph(graph.Nodes.Select(n => n.NodeId == "true" ? n with { NodeTypeId = "fail" } : n), graph.Connections);
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(failing);
        Assert.False(run.Success); Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["true"].Status);
        Assert.Equal(NodeSkipReason.UpstreamUnavailable, run.Nodes["merge"].SkipReason); Assert.Empty(run.Nodes["merge"].Outputs); Assert.DoesNotContain("after", effects);
    }

    [Fact]
    public async Task MultipleActiveMergeBranchesAreAnErrorAndZeroAreInactive()
    {
        var effects = new List<string>(); var registry = Registry(effects);
        var graph = BranchGraph(true);
        var both = new NodeGraph(graph.Nodes.Where(n => n.NodeId is not ("condition" or "if")), graph.Connections.Where(e => e.SourceNodeId is not ("condition" or "if")));
        using var invalid = await new RuntimeExecutor(registry).ExecuteAsync(both);
        Assert.Equal(NodeExecutionStatus.Failed, invalid.Nodes["merge"].Status); Assert.Empty(invalid.Nodes["merge"].Outputs);
        var single = new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Inactive, ["FalseControl"] = ControlSignal.Inactive };
        using var none = await new RuntimeExecutor(registry).ExecuteSingleAsync(Spec("merge", "merge"), single);
        Assert.True(none.Success); Assert.Equal(NodeSkipReason.InactiveBranch, none.Nodes["merge"].SkipReason);
    }

    [Fact]
    public async Task SingleMergeIgnoresInactiveMissingValueAndRequiresActiveValue()
    {
        var effects = new List<string>(); var executor = new RuntimeExecutor(Registry(effects));
        using var run = await executor.ExecuteSingleAsync(Spec("merge", "merge"), new Dictionary<string, object>
        { ["TrueControl"] = ControlSignal.Inactive, ["FalseControl"] = ControlSignal.Active, ["FalseValue"] = "current" });
        Assert.Equal("current", run.Nodes["merge"].Output<string>("Value"));
        using var missing = await executor.ExecuteSingleAsync(Spec("merge", "merge"), new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Active });
        Assert.Equal(NodeExecutionStatus.Failed, missing.Status); Assert.Empty(missing.Nodes["merge"].Outputs);
    }

    [Fact]
    public async Task CancellationNeverActivatesTheOtherBranch()
    {
        var effects = new List<string>(); var registry = Registry(effects); using var cancellation = new CancellationTokenSource();
        registry.Register("cancel", () => new TestNode("cancel", [], [new("Value", typeof(string))], _ =>
        { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return new(new Dictionary<string, object>()); }));
        var graph = BranchGraph(true);
        using var run = await new RuntimeExecutor(registry).ExecuteAsync(new(graph.Nodes.Select(n => n.NodeId == "true" ? n with { NodeTypeId = "cancel" } : n), graph.Connections), cancellationToken: cancellation.Token);
        Assert.Equal(NodeExecutionStatus.Cancelled, run.Status); Assert.Empty(effects); Assert.Empty(run.Nodes["merge"].Outputs);
    }

    [Fact]
    public void ControlAndNumericPortConstraintsRejectInvalidConnections()
    {
        var effects = new List<string>(); var registry = Registry(effects);
        registry.Register("number", () => new TestNode("number", [new("Number", typeof(object)) { Constraint = PortValueConstraint.Numeric }], [], _ => new(new Dictionary<string, object>())));
        var validator = new GraphValidator(registry);
        Assert.Contains(validator.Validate(new([Spec("source", "effect"), Spec("target", "number")], [Edge("source", "Value", "target", "Number")])), e => e.Contains("type mismatch"));
        Assert.Contains(validator.Validate(new([Spec("source", "effect"), Spec("target", "effect")], [Edge("source", "Value", "target", NodePorts.Execute)])), e => e.Contains("type mismatch"));
        Assert.Contains(validator.Validate(new([Spec("source", "effect"), Spec("target", "consume")], [Edge("source", NodePorts.Completed, "target", "Value")])), e => e.Contains("type mismatch"));
    }

    private sealed record ConditionParameters(bool Value = false);
    private sealed class TestNode(string type, IReadOnlyList<PortDefinition> inputs, IReadOnlyList<PortDefinition> outputs,
        Func<NodeExecutionContext, NodeOutput> execute, IReadOnlyList<string>? validationErrors = null) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new(type, "1.0.0", inputs, outputs);
        public IReadOnlyList<string> ValidateParameters(System.Text.Json.JsonElement parameters) => validationErrors ?? [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) => ValueTask.FromResult(execute(context));
    }
    private sealed class TestMerge : IExecutableNode, IExclusiveBranchMergeNode
    {
        public IReadOnlyList<BranchMergeInput> Branches { get; } = [new("TrueControl", "TrueValue"), new("FalseControl", "FalseValue")];
        public NodeDescriptor Descriptor => new("merge", "1.0.0", [NodePorts.Control("TrueControl", true), new("TrueValue", typeof(string)), NodePorts.Control("FalseControl", true), new("FalseValue", typeof(string))], [new("Value", typeof(string))]);
        public IReadOnlyList<string> ValidateParameters(System.Text.Json.JsonElement parameters) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            var selected = Branches.Single(b => context.TryInput<ControlSignal>(b.ControlPort, out var signal) && signal.IsActive);
            return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Value"] = context.Input<string>(selected.ValuePort) }));
        }
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Runtime.Execution;

/// <summary>Serial data/control DAG execution with explicit current-run values and resource ownership.</summary>
public sealed class RuntimeExecutor(NodeRegistry registry, ILogger? logger, IExecutionServices? services)
{
    public RuntimeExecutor(NodeRegistry registry, ILogger? logger = null) : this(registry, logger, null) { }
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    public long ImageBudgetBytes { get; init; } = long.MaxValue;

    public Task<RunResult> ExecuteAsync(NodeGraph graph, string resourceRoot = ".", CancellationToken cancellationToken = default)
        => ExecutePlanAsync(new GraphValidator(registry).Compile(graph, false), resourceRoot, null, cancellationToken);

    public Task<RunResult> ExecuteToAsync(NodeGraph graph, string targetNodeId, string resourceRoot = ".", CancellationToken cancellationToken = default)
    {
        if (graph.Nodes.Any(n => n.NodeId == targetNodeId && !n.IsEnabled))
            throw new InvalidOperationException("The selected node is disabled and cannot run.");
        var plan = new GraphValidator(registry).Compile(graph, false);
        if (!plan.Nodes.ContainsKey(targetNodeId)) throw new ArgumentException($"Unknown target node '{targetNodeId}'.", nameof(targetNodeId));
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(targetNodeId);
        while (pending.TryPop(out var id))
        {
            if (!selected.Add(id)) continue;
            foreach (var edge in plan.Incoming[id]) pending.Push(edge.SourceNodeId);
        }
        return ExecutePlanAsync(plan, resourceRoot, selected, cancellationToken);
    }

    public async Task<RunResult> ExecuteSingleAsync(NodeSpecification specification,
        IReadOnlyDictionary<string, object> inputSnapshot, string resourceRoot = ".", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputSnapshot);
        if (!specification.IsEnabled) throw new InvalidOperationException("The selected node is disabled and cannot run.");
        var node = registry.Create(specification.NodeTypeId);
        if (node.Descriptor.Version != specification.PluginVersion)
            throw new GraphValidationException([$"Node '{specification.NodeId}' requires plugin version '{specification.PluginVersion}', installed '{node.Descriptor.Version}'."]);
        var run = new RunResult();
        var watch = Stopwatch.StartNew();
        RuntimeExecutionSession? ownedSession = services is null ? new() : null;
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, (services ?? ownedSession!).LifetimeCancellation);
        cancellationToken = sessionCancellation.Token;
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                run.Add(Empty(specification.NodeId, NodeExecutionStatus.Cancelled, "Execution was cancelled before the node started."));
                Complete(run, watch.Elapsed); return run;
            }
            var snapshot = new Dictionary<string, object>(inputSnapshot, StringComparer.Ordinal);
            if (snapshot.TryGetValue(NodePorts.Execute, out var gate))
            {
                if (gate is not ControlSignal signal) throw new GraphValidationException(["$exec requires a control signal."]);
                snapshot.Remove(NodePorts.Execute);
                if (!signal.IsActive)
                {
                    run.Add(Inactive(specification.NodeId, "Explicit single-node control snapshot is inactive."));
                    Complete(run, watch.Elapsed); return run;
                }
            }
            var mergeBranches = (node as IExclusiveBranchMergeNode)?.Branches;
            var mergeControls = mergeBranches?.Select(b => b.ControlPort).ToHashSet(StringComparer.Ordinal) ?? [];
            var ordinaryControls = node.Descriptor.InputPorts.Where(p => p.Kind == PortKind.Control && !mergeControls.Contains(p.Name));
            if (ordinaryControls.Any(p => snapshot.TryGetValue(p.Name, out var value) && value is ControlSignal { IsActive: false }) ||
                mergeBranches is not null && mergeBranches.Any(b => snapshot.ContainsKey(b.ControlPort)) &&
                mergeBranches.All(b => !snapshot.TryGetValue(b.ControlPort, out var value) || value is ControlSignal { IsActive: false }))
            {
                run.Add(Inactive(specification.NodeId, "Explicit single-node control snapshot is inactive."));
                Complete(run, watch.Elapsed); return run;
            }
            var errors = ValidateInputs(node, snapshot, specification.Parameters);
            if (errors.Count != 0) throw new GraphValidationException(errors);
            var result = await ExecuteNodeAsync(node, specification, new ReadOnlyDictionary<string, object>(snapshot),
                resourceRoot, run, services ?? ownedSession!, cancellationToken).ConfigureAwait(false);
            run.Add(result); Complete(run, watch.Elapsed); return run;
        }
        catch { run.Dispose(); throw; }
        finally { await CloseOwnedSessionAsync(ownedSession, run).ConfigureAwait(false); }
    }

    /// <summary>Transfers snapshot resources to this run, including on validation failure. The source
    /// algorithm is replaced only for this invocation; no camera or communication operation is invoked.</summary>
    public Task<RunResult> ExecuteWithSourceAsync(NodeGraph graph, string sourceNodeId, NodeOutput sourceSnapshot,
        string resourceRoot = ".", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        GraphValidator.ExecutionPlan plan;
        try
        {
            plan = new GraphValidator(registry).Compile(graph, false);
            if (!plan.Specifications.TryGetValue(sourceNodeId, out var spec) || !spec.IsEnabled)
                throw new ArgumentException("The snapshot source must be an enabled node in the current graph.", nameof(sourceNodeId));
            if (plan.Nodes[sourceNodeId].Descriptor.InputPorts.Any(p => p.Kind == PortKind.Data))
                throw new ArgumentException("A snapshot can replace only a data source with no data inputs.", nameof(sourceNodeId));
            var errors = ValidateOutputs(plan.Nodes[sourceNodeId].Descriptor, sourceSnapshot.Values);
            if (errors.Count > 0) throw new GraphValidationException(errors);
        }
        catch
        {
            using var cleanup = new RunResult(); cleanup.Own(sourceSnapshot.OwnedResources);
            throw;
        }
        return ExecutePlanAsync(plan, resourceRoot, null, cancellationToken, sourceNodeId, sourceSnapshot);
    }

    private async Task<RunResult> ExecutePlanAsync(GraphValidator.ExecutionPlan plan, string resourceRoot,
        HashSet<string>? selected, CancellationToken cancellationToken, string? sourceNodeId = null, NodeOutput? sourceSnapshot = null)
    {
        var run = new RunResult();
        if (sourceSnapshot is not null) run.Own(sourceSnapshot.OwnedResources);
        var watch = Stopwatch.StartNew();
        RuntimeExecutionSession? ownedSession = services is null ? new() : null;
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, (services ?? ownedSession!).LifetimeCancellation);
        cancellationToken = sessionCancellation.Token;
        try
        {
            foreach (var id in plan.Order)
            {
                if (selected is not null && !selected.Contains(id)) continue;
                if (!plan.Specifications[id].IsEnabled)
                {
                    run.Add(Empty(id, NodeExecutionStatus.Skipped, "Node is disabled.") with { SkipReason = NodeSkipReason.Disabled });
                    continue;
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    run.Add(Empty(id, NodeExecutionStatus.Cancelled, "Execution was cancelled before the node started."));
                    continue;
                }
                var node = plan.Nodes[id];
                var preparation = PrepareInputs(id, node, plan.Incoming[id], run);
                if (preparation.Skip is { } skipped) { run.Add(skipped); continue; }
                if (id == sourceNodeId && sourceSnapshot is not null)
                {
                    run.Add(new NodeExecutionResult(id, NodeExecutionStatus.Success, TimeSpan.Zero, null, sourceSnapshot.Values));
                    continue;
                }
                var result = await ExecuteNodeAsync(node, plan.Specifications[id], preparation.Inputs!, resourceRoot,
                    run, services ?? ownedSession!, cancellationToken).ConfigureAwait(false);
                run.Add(result);
            }
            Complete(run, watch.Elapsed);
            return run;
        }
        catch
        {
            run.Dispose();
            throw;
        }
        finally { await CloseOwnedSessionAsync(ownedSession, run).ConfigureAwait(false); }
    }

    private async ValueTask<NodeExecutionResult> ExecuteNodeAsync(IExecutableNode node, NodeSpecification spec,
        IReadOnlyDictionary<string, object> inputs, string resourceRoot, RunResult run, IExecutionServices executionServices, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        NodeOutput? output = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputErrors = ValidateInputs(node, inputs, spec.Parameters);
            if (inputErrors.Count != 0) return Empty(spec.NodeId, NodeExecutionStatus.Failed, string.Join("; ", inputErrors), watch.Elapsed);
            var errors = node.ValidateParameters(spec.Parameters);
            if (errors.Count != 0) return Empty(spec.NodeId, NodeExecutionStatus.Failed, string.Join("; ", errors), watch.Elapsed);
            executionServices.GetOrCreate<ISubflowRuntime>(ISubflowRuntime.ServiceKey, () => new SubflowRuntime(registry));
            output = await node.ExecuteAsync(new NodeExecutionContext(spec, inputs, resourceRoot, executionServices, run.RunId), cancellationToken).ConfigureAwait(false);
            if (output is null) throw new InvalidOperationException("Node returned no output object.");
            var outputErrors = ValidateOutputs(node.Descriptor, output.Values);
            var borrowed = inputs.Values.ToHashSet(ReferenceEqualityComparer.Instance);
            if (output.OwnedResources.Any(borrowed.Contains)) outputErrors.Add("Node attempted to take ownership of a borrowed input.");
            if (outputErrors.Count != 0) throw new InvalidOperationException(string.Join("; ", outputErrors));
            run.Own(output.OwnedResources);
            if (run.RetainedImageBytes > ImageBudgetBytes) throw new InvalidOperationException("Subflow working image budget exceeded; reduce ROI/output size or increase the explicit budget.");
            if (output.CompletionStatus is { } terminal)
            {
                if (node is not ISubflowContainer || terminal is not (NodeExecutionStatus.Success or NodeExecutionStatus.Failed or NodeExecutionStatus.Cancelled))
                    throw new InvalidOperationException("Only validated finite subflows may return a terminal partial result.");
                return new(spec.NodeId, terminal, watch.Elapsed, output.CompletionError, output.Values);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogDebug("Node {NodeId} succeeded in {ElapsedMs} ms", spec.NodeId, watch.Elapsed.TotalMilliseconds);
            return new NodeExecutionResult(spec.NodeId, NodeExecutionStatus.Success, watch.Elapsed, null, output.Values);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (output is not null) run.Own(output.OwnedResources.Where(r => !inputs.Values.Any(i => ReferenceEquals(i, r))));
            return Empty(spec.NodeId, NodeExecutionStatus.Cancelled, "Execution was cancelled.", watch.Elapsed);
        }
        catch (Exception ex)
        {
            if (output is not null) run.Own(output.OwnedResources.Where(r => !inputs.Values.Any(i => ReferenceEquals(i, r))));
            _logger.LogError(ex, "Node {NodeId} failed", spec.NodeId);
            return Empty(spec.NodeId, NodeExecutionStatus.Failed, ex.Message, watch.Elapsed);
        }
    }

    private enum ControlAvailability { Active, Inactive, Unavailable }

    private static ControlAvailability ControlState(NodeConnection edge, RunResult run)
    {
        var source = run.Nodes[edge.SourceNodeId];
        if (source.SkipReason == NodeSkipReason.InactiveBranch) return ControlAvailability.Inactive;
        if (source.Status != NodeExecutionStatus.Success) return ControlAvailability.Unavailable;
        return !TryRead(edge, run, out var value) || value is ControlSignal { IsActive: false }
            ? ControlAvailability.Inactive : value is ControlSignal ? ControlAvailability.Active : ControlAvailability.Unavailable;
    }

    private static bool TryRead(NodeConnection edge, RunResult run, out object value)
    {
        var source = run.Nodes[edge.SourceNodeId];
        if (source.Status == NodeExecutionStatus.Success && edge.SourcePort == NodePorts.Completed)
        { value = ControlSignal.Active; return true; }
        if (source.Status == NodeExecutionStatus.Success && source.Outputs.TryGetValue(edge.SourcePort, out var output))
        { value = output; return true; }
        value = null!; return false;
    }

    private static (IReadOnlyDictionary<string, object>? Inputs, NodeExecutionResult? Skip) PrepareInputs(
        string id, IExecutableNode node, IReadOnlyList<NodeConnection> incoming, RunResult run)
    {
        var merge = node as IExclusiveBranchMergeNode;
        var branchControls = merge?.Branches.Select(b => b.ControlPort).ToHashSet(StringComparer.Ordinal) ?? [];
        var inputPorts = NodePorts.Inputs(node.Descriptor).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var gates = incoming.Where(e => inputPorts[e.TargetPort].Kind == PortKind.Control && !branchControls.Contains(e.TargetPort)).ToArray();
        // A closed gate wins before data preparation or parameter validation. No algorithm or
        // external side effect can be reached on this path, even when data is absent.
        if (gates.Any(e => ControlState(e, run) == ControlAvailability.Inactive))
            return (null, Inactive(id, "Control branch is not active in this run."));
        var blockedGate = gates.FirstOrDefault(e => ControlState(e, run) == ControlAvailability.Unavailable);
        if (blockedGate is not null)
            return (null, Empty(id, NodeExecutionStatus.Skipped, $"Upstream control '{blockedGate.SourceNodeId}.{blockedGate.SourcePort}' did not succeed."));

        IEnumerable<NodeConnection> selectedEdges = incoming;
        if (merge is not null)
        {
            var controls = merge.Branches.Select(branch => (Branch: branch,
                Edge: incoming.Single(e => e.TargetPort == branch.ControlPort))).ToArray();
            var blocked = controls.FirstOrDefault(pair => ControlState(pair.Edge, run) == ControlAvailability.Unavailable);
            if (blocked.Edge is not null)
                return (null, Empty(id, NodeExecutionStatus.Skipped, $"Merge control source '{blocked.Edge.SourceNodeId}' did not succeed."));
            var active = controls.Where(pair => ControlState(pair.Edge, run) == ControlAvailability.Active).ToArray();
            if (active.Length == 0) return (null, Inactive(id, "No merge branch is active in this run."));
            if (active.Length != 1) return (null, Empty(id, NodeExecutionStatus.Failed, "Merge requires exactly one active branch; multiple controls are active."));
            var branchNames = merge.Branches.SelectMany(b => new[] { b.ControlPort, b.ValuePort }).ToHashSet(StringComparer.Ordinal);
            var chosen = active[0].Branch;
            selectedEdges = incoming.Where(e => !branchNames.Contains(e.TargetPort) || e.TargetPort == chosen.ControlPort || e.TargetPort == chosen.ValuePort);
        }

        var edges = selectedEdges.Where(e => e.TargetPort != NodePorts.Execute).ToArray();
        var inactiveSource = edges.FirstOrDefault(e => run.Nodes[e.SourceNodeId].SkipReason == NodeSkipReason.InactiveBranch);
        if (inactiveSource is not null)
            return (null, merge is null ? Inactive(id, $"Upstream branch '{inactiveSource.SourceNodeId}' is inactive.")
                : Empty(id, NodeExecutionStatus.Skipped, $"Selected merge data '{inactiveSource.SourceNodeId}.{inactiveSource.SourcePort}' is inactive or unavailable."));
        var failedSource = edges.FirstOrDefault(edge => run.Nodes[edge.SourceNodeId].Status != NodeExecutionStatus.Success);
        if (failedSource is not null)
            return (null, Empty(id, NodeExecutionStatus.Skipped, $"Upstream node '{failedSource.SourceNodeId}' did not succeed."));
        var missing = edges.FirstOrDefault(edge => !TryRead(edge, run, out _));
        if (missing is not null)
            return (null, Empty(id, NodeExecutionStatus.Skipped, $"Source output '{missing.SourceNodeId}.{missing.SourcePort}' is unavailable for this run."));
        var values = edges.ToDictionary(e => e.TargetPort, e => { TryRead(e, run, out var value); return value; }, StringComparer.Ordinal);
        return (new ReadOnlyDictionary<string, object>(values), null);
    }

    private async Task CloseOwnedSessionAsync(RuntimeExecutionSession? owned, RunResult run)
    {
        if (owned is null) return;
        try { await owned.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Cleanup must attempt every service; retain inspectable image results even when a
            // plugin service fails to close, and report the failure rather than losing the run.
            if (run.Status != NodeExecutionStatus.Cancelled) run.Status = NodeExecutionStatus.Failed;
            run.ErrorMessage = string.Join("; ", new[] { run.ErrorMessage, "Session cleanup failed: " + ex.Message }.Where(s => !string.IsNullOrEmpty(s)));
            _logger.LogError(ex, "Runtime session cleanup failed");
        }
    }

    private static List<string> ValidateInputs(IExecutableNode node, IReadOnlyDictionary<string, object> inputs, System.Text.Json.JsonElement parameters)
    {
        var ports = node.Descriptor.InputPorts;
        if(node is IParameterDependentInputs dynamicInputs){var required=dynamicInputs.RequiredInputs(parameters);ports=ports.Select(p=>required.Contains(p.Name)?p with{Required=true}:p).ToArray();}
        if (node is IExclusiveBranchMergeNode merge)
        {
            var branchNames = merge.Branches.SelectMany(b => new[] { b.ControlPort, b.ValuePort }).ToHashSet(StringComparer.Ordinal);
            ports = ports.Select(p => branchNames.Contains(p.Name) ? p with { Required = false } : p).ToArray();
        }
        return ValidatePorts(ports, inputs, "input");
    }
    private static List<string> ValidateOutputs(NodeDescriptor descriptor, IReadOnlyDictionary<string, object> outputs)
        => ValidatePorts(descriptor.OutputPorts, outputs, "output");
    private static List<string> ValidatePorts(IReadOnlyList<PortDefinition> ports, IReadOnlyDictionary<string, object> values, string direction)
    {
        var errors = new List<string>();
        foreach (var (name, value) in values)
        {
            var port = ports.SingleOrDefault(p => p.Name == name);
            if (port is null) errors.Add($"Unknown {direction} '{name}'.");
            else if (!NodePorts.Accepts(port, value)) errors.Add($"Invalid {direction} type for '{name}'; expected {(port.Constraint == PortValueConstraint.Numeric ? "Number" : port.DataType.Name)}.");
        }
        foreach (var port in ports.Where(p => p.Required))
            if (!values.ContainsKey(port.Name)) errors.Add($"Required {direction} '{port.Name}' is missing.");
        return errors;
    }

    private static NodeExecutionResult Empty(string id, NodeExecutionStatus status, string error, TimeSpan elapsed = default)
        => new(id, status, elapsed, error, new ReadOnlyDictionary<string, object>(new Dictionary<string, object>()))
        { SkipReason = status == NodeExecutionStatus.Skipped ? NodeSkipReason.UpstreamUnavailable : NodeSkipReason.None };

    private static NodeExecutionResult Inactive(string id, string reason) =>
        Empty(id, NodeExecutionStatus.Skipped, reason) with { SkipReason = NodeSkipReason.InactiveBranch };

    private static void Complete(RunResult run, TimeSpan elapsed)
    {
        run.ExecutionTime = elapsed;
        run.Status = run.Nodes.Values.Any(n => n.Status == NodeExecutionStatus.Cancelled) ? NodeExecutionStatus.Cancelled
            : run.Nodes.Values.Any(n => n.Status == NodeExecutionStatus.Failed || n.Status == NodeExecutionStatus.Skipped && n.SkipReason is not (NodeSkipReason.InactiveBranch or NodeSkipReason.Disabled)) ? NodeExecutionStatus.Failed
            : NodeExecutionStatus.Success;
        run.ErrorMessage = string.Join("; ", run.Nodes.Values.Where(n => n.Status != NodeExecutionStatus.Success && n.SkipReason is not (NodeSkipReason.InactiveBranch or NodeSkipReason.Disabled))
            .Select(n => $"{n.NodeId}: {n.ErrorMessage}"));
        if (run.ErrorMessage.Length == 0) run.ErrorMessage = null;
    }
}

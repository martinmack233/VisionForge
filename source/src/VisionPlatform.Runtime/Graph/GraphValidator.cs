using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Runtime.Graph;

public sealed class GraphValidator(NodeRegistry registry)
{
    public IReadOnlyList<string> Validate(NodeGraph graph, bool validateParameters = true)
    {
        try { Compile(graph, validateParameters); return Array.Empty<string>(); }
        catch (GraphValidationException ex) { return ex.Errors; }
    }

    /// <summary>Checks a saved configuration without requiring a completed executable graph.</summary>
    public IReadOnlyList<string> ValidateStructure(NodeGraph graph, bool validateParameters = true)
    {
        try { Compile(graph, validateParameters, false); return Array.Empty<string>(); }
        catch (GraphValidationException ex) { return ex.Errors; }
    }

    internal ExecutionPlan Compile(NodeGraph graph, bool validateParameters, bool validateRequiredInputs = true)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var errors = new List<string>();
        if (graph.Nodes.Count == 0) errors.Add("Graph must contain at least one node.");
        var specs = new Dictionary<string, NodeSpecification>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, IExecutableNode>(StringComparer.Ordinal);
        foreach (var spec in graph.Nodes)
        {
            if (spec is null) { errors.Add("Node specification must not be null."); continue; }
            if (string.IsNullOrWhiteSpace(spec.NodeId)) { errors.Add("NodeId must be non-empty."); continue; }
            if (!specs.TryAdd(spec.NodeId, spec)) { errors.Add($"Duplicate node ID '{spec.NodeId}'."); continue; }
            try
            {
                var node = registry.Create(spec.NodeTypeId);
                nodes.Add(spec.NodeId, node);
                if (node is IExclusiveBranchMergeNode merge)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    if (merge.Branches.Count < 2) errors.Add($"Merge node '{spec.NodeId}' must declare at least two branches.");
                    foreach (var branch in merge.Branches)
                    {
                        var control = node.Descriptor.InputPorts.SingleOrDefault(p => p.Name == branch.ControlPort);
                        var value = node.Descriptor.InputPorts.SingleOrDefault(p => p.Name == branch.ValuePort);
                        if (!names.Add(branch.ControlPort) || !names.Add(branch.ValuePort) ||
                            control is not { Kind: PortKind.Control, Required: true } || value is not { Kind: PortKind.Data, Required: true })
                            errors.Add($"Merge node '{spec.NodeId}' has an invalid control/value branch declaration.");
                    }
                }
                if (spec.PluginVersion != node.Descriptor.Version)
                    errors.Add($"Node '{spec.NodeId}' requires plugin version '{spec.PluginVersion}', installed '{node.Descriptor.Version}'.");
                if (validateParameters && spec.IsEnabled)
                    errors.AddRange(node.ValidateParameters(spec.Parameters).Select(e => $"Node '{spec.NodeId}': {e}"));
                if (validateParameters && spec.IsEnabled && node is VisionPlatform.Runtime.Execution.ISubflowContainer container)
                    errors.AddRange(VisionPlatform.Runtime.Execution.SubflowValidation.Validate(registry, container.GetSubflow(spec)).Select(e => $"Subflow '{spec.NodeId}': {e}"));
            }
            catch (Exception ex) { errors.Add($"Node '{spec.NodeId}': {ex.Message}"); }
        }

        var inputConnections = new HashSet<(string NodeId, string Port)>();
        var incoming = specs.Keys.ToDictionary(id => id, _ => new List<NodeConnection>(), StringComparer.Ordinal);
        var outgoing = specs.Keys.ToDictionary(id => id, _ => new List<NodeConnection>(), StringComparer.Ordinal);
        foreach (var edge in graph.Connections)
        {
            if (edge is null || string.IsNullOrWhiteSpace(edge.SourceNodeId) || string.IsNullOrWhiteSpace(edge.TargetNodeId))
            { errors.Add("Connection and its node IDs must be non-empty."); continue; }
            if (specs.TryGetValue(edge.SourceNodeId, out var sourceSpec) && !sourceSpec.IsEnabled ||
                specs.TryGetValue(edge.TargetNodeId, out var targetSpec) && !targetSpec.IsEnabled)
            { errors.Add("Disabled nodes cannot have data or control connections."); continue; }
            if (!nodes.TryGetValue(edge.SourceNodeId, out var source) || !nodes.TryGetValue(edge.TargetNodeId, out var target))
            { errors.Add($"Connection '{edge.SourceNodeId}.{edge.SourcePort}' -> '{edge.TargetNodeId}.{edge.TargetPort}' references a missing node."); continue; }
            var output = NodePorts.Outputs(source.Descriptor).SingleOrDefault(p => p.Name == edge.SourcePort);
            var input = NodePorts.Inputs(target.Descriptor).SingleOrDefault(p => p.Name == edge.TargetPort);
            if (output is null) errors.Add($"Unknown output '{edge.SourceNodeId}.{edge.SourcePort}'.");
            if (input is null) errors.Add($"Unknown input '{edge.TargetNodeId}.{edge.TargetPort}'.");
            if (output is not null && input is not null && !NodePorts.Compatible(output, input))
                errors.Add($"Port type mismatch: {edge.SourceNodeId}.{edge.SourcePort} ({output.DataType.Name}) -> {edge.TargetNodeId}.{edge.TargetPort} ({input.DataType.Name}).");
            if (!inputConnections.Add((edge.TargetNodeId, edge.TargetPort)))
                errors.Add($"Input '{edge.TargetNodeId}.{edge.TargetPort}' has multiple sources.");
            incoming[edge.TargetNodeId].Add(edge);
            outgoing[edge.SourceNodeId].Add(edge);
        }
        foreach (var (id, node) in nodes.Where(p => validateRequiredInputs && specs[p.Key].IsEnabled))
        {
            IReadOnlyList<string> conditional=[];
            try{if(node is IParameterDependentInputs dynamicInputs)conditional=dynamicInputs.RequiredInputs(specs[id].Parameters);}catch(Exception ex){errors.Add($"Node '{id}' conditional input configuration: {ex.Message}");}
            if(conditional.Any(name=>!node.Descriptor.InputPorts.Any(p=>p.Name==name)))errors.Add($"Node '{id}' declares an unknown conditional input.");
            foreach (var port in node.Descriptor.InputPorts.Where(p => p.Required||conditional.Contains(p.Name)))
                if (!inputConnections.Contains((id, port.Name))) errors.Add($"Required input '{id}.{port.Name}' is not connected.");
            if (node is VisionPlatform.Runtime.Execution.ISubflowContainer container && validateParameters)
            {
                try { if (VisionPlatform.Runtime.Execution.SubflowValidation.NeedsSharedImage(container.GetSubflow(specs[id])) && !inputConnections.Contains((id, "Image"))) errors.Add($"Required shared image '{id}.Image' is not connected; the subflow reads IterationInput.Image."); }
                catch (Exception ex) { errors.Add($"Subflow '{id}': {ex.Message}"); }
            }
        }

        var indegree = incoming.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.Ordinal);
        var ready = new Queue<string>(graph.Nodes.Where(n => n is not null && !string.IsNullOrWhiteSpace(n.NodeId)).Select(n => n.NodeId).Where(id => indegree.TryGetValue(id, out var d) && d == 0).Distinct(StringComparer.Ordinal));
        var ordered = new List<string>();
        while (ready.TryDequeue(out var id))
        {
            ordered.Add(id);
            foreach (var edge in outgoing[id])
                if (--indegree[edge.TargetNodeId] == 0) ready.Enqueue(edge.TargetNodeId);
        }
        if (ordered.Count != specs.Count) errors.Add("Graph contains a directed cycle.");
        if (errors.Count != 0) throw new GraphValidationException(errors);
        return new ExecutionPlan(specs, nodes, incoming, ordered);
    }

    internal sealed record ExecutionPlan(
        IReadOnlyDictionary<string, NodeSpecification> Specifications,
        IReadOnlyDictionary<string, IExecutableNode> Nodes,
        IReadOnlyDictionary<string, List<NodeConnection>> Incoming,
        IReadOnlyList<string> Order);
}

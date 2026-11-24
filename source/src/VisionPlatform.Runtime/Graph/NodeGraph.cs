using VisionPlatform.Contracts.Nodes;
using System.Text.Json;

namespace VisionPlatform.Runtime.Graph;

/// <summary>A detached graph snapshot. Runtime execution never reads editor objects.</summary>
public sealed class NodeGraph
{
    public IReadOnlyList<NodeSpecification> Nodes { get; }
    public IReadOnlyList<NodeConnection> Connections { get; }

    public NodeGraph(IEnumerable<NodeSpecification> nodes, IEnumerable<NodeConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        Nodes = Array.AsReadOnly(nodes.Select(n => n is null ? null! : n with
        {
            Parameters = n.Parameters.ValueKind == JsonValueKind.Undefined ? default : n.Parameters.Clone()
        }).ToArray());
        Connections = Array.AsReadOnly(connections.ToArray());
    }
}

public sealed class GraphValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public GraphValidationException(IEnumerable<string> errors)
        : this(errors.ToArray()) { }
    private GraphValidationException(string[] errors)
        : base("Graph validation failed: " + string.Join("; ", errors)) => Errors = Array.AsReadOnly(errors);
}

using System.Collections.ObjectModel;
using System.Text.Json;

namespace VisionPlatform.Contracts.Nodes;

public sealed record PortDefinition(string Name, Type DataType, bool Required = true)
{
    public PortKind Kind { get; init; } = PortKind.Data;
    public PortValueConstraint Constraint { get; init; } = PortValueConstraint.None;
}
public sealed record NodeDescriptor(string TypeId, string Version,
    IReadOnlyList<PortDefinition> InputPorts, IReadOnlyList<PortDefinition> OutputPorts);
public sealed record NodeSpecification(string NodeId, string NodeTypeId, string Name,
    JsonElement Parameters, string PluginVersion = "1.0.0")
{
    /// <summary>Disabled nodes retain configuration but have no connections and never execute.</summary>
    public bool IsEnabled { get; init; } = true;
}
public sealed record NodeConnection(string SourceNodeId, string SourcePort,
    string TargetNodeId, string TargetPort);
public enum NodeExecutionStatus { Pending, Running, Success, Failed, Cancelled, Skipped }

public interface IExecutableNode
{
    NodeDescriptor Descriptor { get; }
    IReadOnlyList<string> ValidateParameters(JsonElement parameters);
    ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Inputs are borrowed and read-only. A node must never dispose its inputs.</summary>
public sealed class NodeExecutionContext(NodeSpecification specification,
    IReadOnlyDictionary<string, object> inputs, string resourceRoot,
    IExecutionServices? services, Guid runId = default)
{
    // Preserve the original constructor signature for already compiled consumers.
    public NodeExecutionContext(NodeSpecification specification, IReadOnlyDictionary<string, object> inputs, string resourceRoot)
        : this(specification, inputs, resourceRoot, null, default) { }
    public NodeSpecification Specification { get; } = specification;
    public NodeSpecification Spec => Specification;
    public string ResourceRoot { get; } = resourceRoot;
    public IExecutionServices? Services { get; } = services;
    public Guid RunId { get; } = runId;
    public IReadOnlyDictionary<string, object> Inputs { get; } = inputs;
    public T Input<T>(string name) => Inputs.TryGetValue(name, out var value) && value is T typed
        ? typed : throw new ArgumentException($"Missing or invalid input '{name}', expected {typeof(T).Name}.");
    public bool TryInput<T>(string name, out T value)
    {
        if (Inputs.TryGetValue(name, out var obj) && obj is T typed) { value = typed; return true; }
        value = default!; return false;
    }
    public T Parameters<T>() => Specification.Parameters.Deserialize<T>(JsonDefaults.Options)
        ?? throw new ArgumentException("Missing parameters.");
}

/// <summary>OwnedResources transfers new output resources to the run; borrowed inputs are excluded.</summary>
public sealed class NodeOutput
{
    /// <summary>Optional terminal outcome for finite orchestrators that retain partial results.</summary>
    public NodeExecutionStatus? CompletionStatus { get; init; }
    public string? CompletionError { get; init; }
    public IReadOnlyDictionary<string, object> Values { get; }
    public IReadOnlyList<IDisposable> OwnedResources { get; }
    public NodeOutput(IDictionary<string, object> values, IEnumerable<IDisposable>? ownedResources = null)
    {
        Values = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(values));
        OwnedResources = (ownedResources ?? []).ToArray();
    }
}
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

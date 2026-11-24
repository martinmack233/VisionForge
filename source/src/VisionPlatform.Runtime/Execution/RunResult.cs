using System.Collections.ObjectModel;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Runtime.Execution;

public sealed record NodeExecutionResult(
    string NodeId,
    NodeExecutionStatus Status,
    TimeSpan ExecutionTime,
    string? ErrorMessage,
    IReadOnlyDictionary<string, object> Outputs)
{
    public NodeSkipReason SkipReason { get; init; }
    public T Output<T>(string portName) => Outputs.TryGetValue(portName, out var value) && value is T typed
        ? typed : throw new KeyNotFoundException($"Node '{NodeId}' has no '{portName}' output of type {typeof(T).Name}.");
}

/// <summary>Owns all explicitly returned resources, retained for output inspection until disposed.</summary>
public sealed class RunResult : IDisposable
{
    private readonly List<IDisposable> _ownedResources = [];
    private readonly HashSet<IDisposable> _knownResources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, NodeExecutionResult> _nodes = new(StringComparer.Ordinal);
    private int _disposed;
    public Guid RunId { get; } = Guid.NewGuid();
    public IReadOnlyDictionary<string, NodeExecutionResult> Nodes { get; }
    public TimeSpan ExecutionTime { get; internal set; }
    public NodeExecutionStatus Status { get; internal set; } = NodeExecutionStatus.Pending;
    public bool Success => Status == NodeExecutionStatus.Success;
    public string? ErrorMessage { get; internal set; }
    internal long RetainedImageBytes => _ownedResources.Select(r => r switch
        {
            VisionPlatform.Contracts.Images.ImageFrame image => image.BufferHandle,
            VisionPlatform.Contracts.Images.ColorPlaneFrame color => color.BufferHandle,
            VisionPlatform.Contracts.Images.FloatPlaneFrame numeric => numeric.BufferHandle,
            _ => null
        }).Where(h => h is not null).Distinct().Sum(h => h!.Length) + _nodes.Values.SelectMany(n=>n.Outputs.Values).OfType<VisionPlatform.Contracts.Numerics.NumericMatrix>().Distinct().Sum(m=>(long)m.Rows*m.Columns*8);

    public RunResult() => Nodes = new ReadOnlyDictionary<string, NodeExecutionResult>(_nodes);
    internal void Add(NodeExecutionResult node) => _nodes.Add(node.NodeId, node);
    internal void Own(IEnumerable<IDisposable> resources)
    {
        foreach (var resource in resources)
            if (_knownResources.Add(resource)) _ownedResources.Add(resource);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception>? errors = null;
        for (var i = _ownedResources.Count - 1; i >= 0; i--)
        {
            try { _ownedResources[i].Dispose(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        _ownedResources.Clear();
        _knownResources.Clear();
        if (errors is not null) throw new AggregateException("One or more node resources could not be released.", errors);
    }
}

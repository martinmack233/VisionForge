using System.Reflection;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Runtime.Graph;

/// <summary>Explicit factories and optional public parameterless plugin discovery.</summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, Func<IExecutableNode>> _factories = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> RegisteredTypeIds => _factories.Keys.ToArray();

    public void Register(string nodeTypeId, Func<IExecutableNode> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeTypeId);
        ArgumentNullException.ThrowIfNull(factory);
        var node = factory() ?? throw new ArgumentException("Node factory returned null.", nameof(factory));
        if (!string.Equals(node.Descriptor.TypeId, nodeTypeId, StringComparison.Ordinal))
            throw new ArgumentException($"Factory descriptor '{node.Descriptor.TypeId}' differs from registration '{nodeTypeId}'.");
        ValidateDescriptor(node.Descriptor);
        if (!_factories.TryAdd(nodeTypeId, factory))
            throw new ArgumentException($"Node type '{nodeTypeId}' is already registered.", nameof(nodeTypeId));
    }

    public void Register(Func<IExecutableNode> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Register(factory().Descriptor.TypeId, factory);
    }

    public IExecutableNode Create(string nodeTypeId)
    {
        if (!_factories.TryGetValue(nodeTypeId, out var factory))
            throw new KeyNotFoundException($"Node type '{nodeTypeId}' is not installed.");
        var node = factory() ?? throw new InvalidOperationException($"Factory for '{nodeTypeId}' returned null.");
        if (node.Descriptor.TypeId != nodeTypeId)
            throw new InvalidOperationException($"Factory for '{nodeTypeId}' returned another node type.");
        ValidateDescriptor(node.Descriptor);
        return node;
    }

    public int Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var count = 0;
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IExecutableNode).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null) continue;
            Register(() => (IExecutableNode)Activator.CreateInstance(type)!);
            count++;
        }
        return count;
    }

    private static void ValidateDescriptor(NodeDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.TypeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Version);
        foreach (var ports in new[] { descriptor.InputPorts, descriptor.OutputPorts })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in ports)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(port.Name);
                if (port.Name is NodePorts.Execute or NodePorts.Completed)
                    throw new ArgumentException("$exec and $done are reserved virtual control ports.");
                if (!Enum.IsDefined(port.Kind) || !Enum.IsDefined(port.Constraint) ||
                    (port.Kind == PortKind.Control && (port.DataType != typeof(ControlSignal) || port.Constraint != PortValueConstraint.None)))
                    throw new ArgumentException("Port control/data metadata is invalid.");
                if (port.DataType is null || !seen.Add(port.Name))
                    throw new ArgumentException($"Node '{descriptor.TypeId}' has duplicate or invalid port '{port.Name}'.");
            }
        }
    }
}

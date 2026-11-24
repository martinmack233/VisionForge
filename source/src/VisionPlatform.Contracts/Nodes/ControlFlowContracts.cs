namespace VisionPlatform.Contracts.Nodes;

public enum PortKind { Data, Control }
public enum PortValueConstraint { None, Numeric }
public enum NodeSkipReason { None, InactiveBranch, UpstreamUnavailable, Disabled }
public sealed record ControlSignal(bool IsActive = true)
{
    public static ControlSignal Active { get; } = new();
    public static ControlSignal Inactive { get; } = new(false);
}

/// <summary>Merge waits for all dependencies but consumes only one current active branch.</summary>
public sealed record BranchMergeInput(string ControlPort, string ValuePort);
public interface IExclusiveBranchMergeNode
{
    IReadOnlyList<BranchMergeInput> Branches { get; }
}

/// <summary>Virtual control ports preserve all existing node descriptor/data semantics and JSON edges.</summary>
public static class NodePorts
{
    public const string Execute = "$exec";
    public const string Completed = "$done";
    public static PortDefinition Control(string name, bool required = false) => new(name, typeof(ControlSignal), required) { Kind = PortKind.Control };
    public static IReadOnlyList<PortDefinition> Inputs(NodeDescriptor descriptor) => descriptor.InputPorts.Concat([Control(Execute)]).ToArray();
    public static IReadOnlyList<PortDefinition> Outputs(NodeDescriptor descriptor) => descriptor.OutputPorts.Concat([Control(Completed)]).ToArray();
    public static bool IsNumeric(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
        type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    public static bool Compatible(PortDefinition output, PortDefinition input) => output.Kind == input.Kind &&
        (input.Constraint == PortValueConstraint.Numeric ? IsNumeric(output.DataType) || output.Constraint == PortValueConstraint.Numeric : input.DataType.IsAssignableFrom(output.DataType));
    public static bool Accepts(PortDefinition port, object? value) => value is not null &&
        (port.Constraint == PortValueConstraint.Numeric ? IsNumeric(value.GetType()) : port.DataType.IsInstanceOfType(value));
}

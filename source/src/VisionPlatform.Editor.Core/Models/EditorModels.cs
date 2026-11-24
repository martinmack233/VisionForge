using System.Text.Json;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Common;

namespace VisionPlatform.Editor.Core.Models;

public sealed class EditorNode : ObservableObject
{
    private NodeSpecification _specification;
    private double _x, _y;
    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    private string _status = "未运行", _error = "";
    private double _milliseconds;
    public NodeSpecification Specification => _specification;
    public string NodeId => _specification.NodeId;
    public string TypeId => _specification.NodeTypeId;
    public string Name => _specification.Name;
    public bool IsEnabled => _specification.IsEnabled;
    public JsonElement Parameters => _specification.Parameters;
    public NodeDescriptor Descriptor { get; }
    public IReadOnlyList<EditorPort> Inputs { get; }
    public IReadOnlyList<EditorPort> Outputs { get; }
    public IReadOnlyList<EditorPort> AllInputs { get; }
    public IReadOnlyList<EditorPort> AllOutputs { get; }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string ErrorMessage { get => _error; set => Set(ref _error, value); }
    public double ExecutionMilliseconds { get => _milliseconds; set => Set(ref _milliseconds, value); }
    public EditorNode(NodeSpecification specification, NodeDescriptor descriptor, double x = 0, double y = 0)
    {
        _specification = specification with { Parameters = specification.Parameters.Clone() };
        Descriptor = descriptor; _x = x; _y = y;
        if (!specification.IsEnabled) _status = "已禁用";
        Inputs = descriptor.InputPorts.Select(p => new EditorPort(this, p, true)).ToArray();
        Outputs = descriptor.OutputPorts.Select(p => new EditorPort(this, p, false)).ToArray();
        AllInputs = Inputs.Concat([new EditorPort(this, NodePorts.Control(NodePorts.Execute), true)]).ToArray();
        AllOutputs = Outputs.Concat([new EditorPort(this, NodePorts.Control(NodePorts.Completed), false)]).ToArray();
    }
    public void ApplySpecification(NodeSpecification specification)
    {
        if (specification.NodeId != NodeId || specification.NodeTypeId != TypeId) throw new ArgumentException("Node identity cannot change.");
        _specification = specification with { Parameters = specification.Parameters.Clone() };
        Notify(nameof(Specification)); Notify(nameof(Parameters)); Notify(nameof(Name)); Notify(nameof(IsEnabled));
    }
}
public sealed record EditorPort(EditorNode Node, PortDefinition Definition, bool IsInput)
{
    public string Name => Definition.Name;
    public Type DataType => Definition.DataType;
    public bool IsControl => Definition.Kind == PortKind.Control;
    public string TypeName => IsControl ? "控制" : Definition.Constraint == PortValueConstraint.Numeric ? "数值" : DataType.Name;
    public bool Required => Definition.Required;
}
public sealed record EditorConnection(EditorPort Source, EditorPort Target)
{
    public NodeConnection ToContract() => new(Source.Node.NodeId, Source.Name, Target.Node.NodeId, Target.Name);
    public string Label => $"{Source.Node.Name}.{Source.Name} → {Target.Node.Name}.{Target.Name}";
}
public sealed record NodeResultRow(string NodeId, string Name, string Status, double Milliseconds, string Error);
public sealed record OutputValueRow(string NodeId, string PortName, string Summary, object Value)
{
    public string Label => $"{NodeId}.{PortName} — {Summary}";
    public string Category => OutputInspector.Category(Value);
    public string DataType => Value.GetType().Name;
}
public enum OverlayKind { ReferenceRoi, FollowedRoi, Match, Blob }
public sealed record OverlayItem(string Label, RotatedRectRoi Region, OverlayKind Kind, bool IsValid = true);
// Shape kinds are an editor extension point; Schema v1 persists only rectangle geometry.
public enum RoiGeometryKind { Rectangle, RotatedRectangle, Circle, Ellipse, Polygon, LineSegment }
public interface IRoiEditGeometry { RoiGeometryKind Kind { get; } string CoordinateSpaceId { get; } }
public sealed record RectangleEditGeometry(RotatedRectRoi Region) : IRoiEditGeometry
{
    public RoiGeometryKind Kind => Math.Abs(Region.AngleDegrees) < 1e-9 ? RoiGeometryKind.Rectangle : RoiGeometryKind.RotatedRectangle;
    public string CoordinateSpaceId => Region.CoordinateSpaceId;
}

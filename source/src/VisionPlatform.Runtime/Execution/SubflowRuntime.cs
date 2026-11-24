using System.Collections.ObjectModel;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Runtime.Execution;

public interface ISubflowContainer { SubflowDefinition GetSubflow(NodeSpecification spec); }
public interface ISubflowRuntime
{
    const string ServiceKey = "VisionPlatform.Runtime.Subflow.v1";
    Task<RunResult> RunAsync(SubflowDefinition definition, NodeOutput input, string root, long imageBudgetBytes, CancellationToken cancellationToken);
    IReadOnlyList<string> Validate(SubflowDefinition definition);
}
internal sealed class SubflowRuntime(NodeRegistry registry) : ISubflowRuntime
{
    public IReadOnlyList<string> Validate(SubflowDefinition body) => SubflowValidation.Validate(registry, body);
    public Task<RunResult> RunAsync(SubflowDefinition body, NodeOutput input, string root, long imageBudgetBytes, CancellationToken ct)
    {
        var errors = Validate(body);
        if (errors.Count > 0) throw new GraphValidationException(errors);
        return new RuntimeExecutor(registry) { ImageBudgetBytes = imageBudgetBytes }.ExecuteWithSourceAsync(new(body.Nodes, body.Connections), body.InputNodeId, input, root, ct);
    }
}
public static class SubflowValidation
{
    public static bool NeedsSharedImage(SubflowDefinition body) => body.Connections.Any(c => c.SourceNodeId == body.InputNodeId && c.SourcePort == "Image");
    public static IReadOnlyList<string> Validate(NodeRegistry registry, SubflowDefinition body)
    {
        var errors = new List<string>();
        if (body.Version != 1) errors.Add("Unsupported subflow version; expected 1.");
        if (body.Nodes is null || body.Connections is null || body.Nodes.Count > 128) return ["Subflow requires Nodes/Connections and at most 128 nodes."];
        foreach (var spec in body.Nodes)
        {
            try
            {
                var node = registry.Create(spec.NodeTypeId);
                if (node is ISubflowContainer) errors.Add("Nested ForEach subflows are not supported.");
                else if (!Attribute.IsDefined(node.GetType(), typeof(SubflowSafeAttribute)))
                    errors.Add($"Subflow node '{spec.NodeId}' ({spec.NodeTypeId}) is not an approved pure algorithm; camera/communication/external effects are forbidden.");
                else foreach (var port in ((SubflowSafeAttribute)Attribute.GetCustomAttribute(node.GetType(), typeof(SubflowSafeAttribute))!).RequiredInputs)
                    if (!body.Connections.Any(c => c.TargetNodeId == spec.NodeId && c.TargetPort == port))
                        errors.Add($"Subflow '{spec.NodeId}' requires an explicit {port} connection; absent ROI must not fall back to the full image.");
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        if (body.Nodes.Count(n => n.NodeTypeId == "IterationInput" && n.IsEnabled) != 1 ||
            !body.Nodes.Any(n => n.NodeId == body.InputNodeId && n.NodeTypeId == "IterationInput" && n.IsEnabled))
            errors.Add("Subflow requires exactly one enabled IterationInput at InputNodeId.");
        if (body.Nodes.Count(n => n.NodeTypeId == "IterationOutput" && n.IsEnabled) != 1 ||
            !body.Nodes.Any(n => n.NodeId == body.OutputNodeId && n.NodeTypeId == "IterationOutput" && n.IsEnabled))
            errors.Add("Subflow requires exactly one enabled IterationOutput at OutputNodeId.");
        if (body.Nodes.Any(n => n.NodeId == body.OutputNodeId && n.IsEnabled) && !body.Connections.Any(c => c.TargetNodeId == body.OutputNodeId &&
            c.TargetPort is "OCR" or "Measurement" or "Decision" or "Text" or "Number" or "IsOk"))
            errors.Add($"Subflow '{body.OutputNodeId}' must collect an explicit result; connect OCR, Measurement, Decision, Text, Number or IsOk.");
        if (errors.Count == 0) errors.AddRange(new GraphValidator(registry).Validate(new(body.Nodes, body.Connections)));
        return errors;
    }
}
public sealed record IterationTrace(int Index, Guid RunId, IReadOnlyList<NodeExecutionResult> Nodes);
/// <summary>Bounded shared image leases. Disposed with the parent RunResult, never a subsequent input.</summary>
public sealed class IterationTraceStore(int maximumIterations, long imageBudgetBytes) : IDisposable
{
    private readonly List<IterationTrace> _traces = [];
    private readonly List<IDisposable> _images = [];
    private readonly HashSet<FrameHandle> _handles = [];
    private readonly HashSet<VisionPlatform.Contracts.Numerics.NumericMatrix> _matrices = [];
    public IReadOnlyList<IterationTrace> Traces => _traces.AsReadOnly();
    public long RetainedBytes { get; private set; }
    public void Capture(int index, RunResult run)
    {
        if (_traces.Count >= maximumIterations) return;
        var nodes = new List<NodeExecutionResult>();
        foreach (var node in run.Nodes.Values)
        {
            var values = new Dictionary<string, object>();
            foreach (var (port, value) in node.Outputs)
            {
                if (value is ImageFrame image)
                {
                    var size = image.BufferHandle.Length;
                    if (!_handles.Contains(image.BufferHandle) && RetainedBytes + size > imageBudgetBytes) continue;
                    var share = image.Share(); _images.Add(share); values.Add(port, share);
                    if (_handles.Add(image.BufferHandle)) RetainedBytes += size;
                }
                else if(value is FloatPlaneFrame plane)
                {
                    var size=plane.BufferHandle.Length;if(!_handles.Contains(plane.BufferHandle)&&RetainedBytes+size>imageBudgetBytes)continue;
                    var share=plane.Share();_images.Add(share);values.Add(port,share);if(_handles.Add(plane.BufferHandle))RetainedBytes+=size;
                }
                else if(value is VisionPlatform.Contracts.Numerics.NumericMatrix matrix){var size=(long)matrix.Rows*matrix.Columns*8;if(!_matrices.Contains(matrix)&&RetainedBytes+size>imageBudgetBytes)continue;if(_matrices.Add(matrix))RetainedBytes+=size;values.Add(port,matrix);}
                else if (value is not IDisposable) values.Add(port, value);
            }
            nodes.Add(node with { Outputs = new ReadOnlyDictionary<string, object>(values) });
        }
        _traces.Add(new(index, run.RunId, nodes.AsReadOnly()));
    }
    public void Dispose() { foreach (var image in _images) image.Dispose(); _images.Clear(); _traces.Clear(); _handles.Clear(); _matrices.Clear(); RetainedBytes = 0; }
}

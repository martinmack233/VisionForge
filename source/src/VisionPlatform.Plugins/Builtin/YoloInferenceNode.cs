using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Plugins.Builtin;

public sealed class YoloInferenceNode : NodeBase<YoloInferenceOptions>
{
    private readonly IYoloInference? _inference;
    public YoloInferenceNode() { }
    public YoloInferenceNode(IYoloInference inference) => _inference = inference;
    public override NodeDescriptor Descriptor => Describe("YoloInference", [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("Result", typeof(YoloInferenceResult)), new("Count", typeof(int)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(YoloInferenceOptions options) => YoloInferenceValidation.Validate(options);
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var image = context.Input<ImageFrame>("Image");
        var options = context.Parameters<YoloInferenceOptions>();
        if (!string.IsNullOrWhiteSpace(options.ModelPath)) options = options with { ModelPath = VisionPlatform.Runtime.Projects.ProjectStore.ResolveResourcePath(context.ResourceRoot, options.ModelPath) };
        options = options with { RunIdentity = context.RunId.ToString("N") };
        var resident = _inference is null && options.Mode == YoloExecutionMode.Resident ? (context.Services ?? throw new InvalidOperationException("Resident YOLO requires an explicit Runtime session.")).GetOrCreate(ResidentYoloBridge.ServiceKey, () => new ResidentYoloBridge()) : null;
        var inference = _inference ?? new PythonYoloInference(resident);
        var result = await inference.InferAsync(image, options,
            context.TryInput<RotatedRectRoi>("ROI", out var roi) ? roi : null, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.FailureReason ?? "YOLO inference failed.");
        result = result with { FrameId = image.FrameId, RunId = context.RunId,
            TaskId = string.IsNullOrEmpty(result.TaskId) ? context.RunId.ToString("N") : result.TaskId,
            AIProjectId = options.AIProjectId, AIWorkspaceId = options.AIWorkspaceId, ModelRecordId = options.ModelRecordId };
        return new NodeOutput(new Dictionary<string, object> { ["Result"] = result, ["Count"] = result.Detections.Count, ["Success"] = result.Success });
    }
}

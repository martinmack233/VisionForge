using System.Text.Json;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.AI.Deployment;

public sealed record VerifiedAiDeployment(VerifiedYoloDeployment Artifact, string WorkspaceId = "", string ProjectId = "", string ModelRecordId = "")
{
    public string ModelPath => Artifact.ModelPath;
    public string ModelSha256 => Artifact.ModelSha256;
    public string DeploymentSha256 => Artifact.DeploymentSha256;
    public int InputSize => Artifact.InputSize;
    public IReadOnlyDictionary<int, string> Names => Artifact.Names;
    public string DeploymentJson => Artifact.Deployment.GetRawText();
    public string PreprocessingJson => Artifact.Deployment.GetProperty("preprocessing").GetRawText();
}

public sealed record AiNodeApplyOptions
{
    public string ProjectId { get; init; } = "";
    public string WorkspaceId { get; init; } = "";
    public string ModelRecordId { get; init; } = "";
    public double? Confidence { get; init; }
    public double? Iou { get; init; }
    public double? MergeIou { get; init; }
    public string ClassIds { get; init; } = "";
    public bool? Tiled { get; init; }
    public int? TileWidth { get; init; }
    public int? TileHeight { get; init; }
    public double? Overlap { get; init; }
    public int BatchSize { get; init; } = 1;
}

/// <summary>UI-independent model authoring. It never executes a graph or creates an inference session.</summary>
public static class AiModelDeployment
{
    public static async Task<VerifiedAiDeployment> VerifyAsync(IAiWorkspaceClient client, string modelPath,
        int? imageSize = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.IsRunning) throw new InvalidOperationException("请先连接 AI 业务服务并确认模型环境。");
        var local = await YoloDeploymentVerifier.VerifyAsync(modelPath, imageSize, cancellationToken).ConfigureAwait(false);
        var response = await client.CallAsync("deployment.inspect", new { project_id = projectId, model_path = local.ModelPath,
            backend = "onnx", imgsz = local.InputSize, batch = 1 }, cancellationToken).ConfigureAwait(false);
        if (!response.GetProperty("validated").GetBoolean() || response.GetProperty("backend").GetString() != "onnx" ||
            response.GetProperty("provider").GetString() != "ONNX Runtime CPUExecutionProvider")
            throw new InvalidDataException("模型未通过现有 ONNX CPU 后端验证；不会静默改用 PT/CUDA。");
        if (!string.Equals(Path.GetFullPath(response.GetProperty("model_path").GetString()!), local.ModelPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.GetProperty("model_sha256").GetString(), local.ModelSha256, StringComparison.OrdinalIgnoreCase) ||
            response.GetProperty("input_size").GetInt32() != local.InputSize)
            throw new InvalidDataException("Python服务与当前本地模型身份/输入尺寸不一致。");
        if (response.TryGetProperty("deployment_sha256", out var deploymentHash) &&
            !string.Equals(deploymentHash.GetString(), local.DeploymentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("服务返回的部署配置哈希不一致。");
        if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(local.Deployment.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(response.GetProperty("deployment").GetRawText())))
            throw new InvalidDataException("服务返回的部署、类别和预处理契约与本地文件不同。");
        return new(local, String(response, "workspace_id"), String(response, "project_id"), String(response, "model_record_id"));
    }

    /// <summary>The caller resolves any unapplied parameter draft before invoking this configuration command.</summary>
    public static EditorNode ApplyToNode(IEditorSession session, VerifiedAiDeployment verified, AiServiceSettings settings,
        AiNodeApplyOptions? applyOptions = null, bool createNew = false, bool portableEnvironment = false)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(verified); ArgumentNullException.ThrowIfNull(settings);
        if (session.IsBusy) throw new InvalidOperationException("检测流程运行中不能替换模型。请先停止流程。");
        if (!File.Exists(verified.ModelPath) || !File.Exists(verified.Artifact.DeploymentPath)) throw new FileNotFoundException("已验证的模型或 deployment.json 已不存在，请重新选择验证。");
        if (!File.Exists(settings.PythonPath) || !Directory.Exists(settings.YoloRoot)) throw new ArgumentException("配置的 Python 或 YOLOStudio 代码目录不存在。");
        var selected = createNew ? null : session.SelectedNode;
        if (!createNew && selected?.TypeId != "YoloInference") throw new InvalidOperationException("请先在算法流程中选中 YOLO 检测节点，或明确选择创建节点。");
        var current = selected?.Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options) ?? new();
        var p = applyOptions ?? new(); var d = verified.Artifact.Deployment;
        var slicing = d.TryGetProperty("slicing", out var sourceSlicing) ? sourceSlicing : default;
        var post = d.GetProperty("postprocess");
        var options = current with
        {
            PythonExecutable = portableEnvironment ? "" : Path.GetFullPath(settings.PythonPath), YoloStudioRoot = portableEnvironment ? "" : Path.GetFullPath(settings.YoloRoot),
            ModelPath = verified.ModelPath, ExecutionBackend = "onnx-cpu", ImageSize = verified.InputSize,
            ExpectedModelSha256 = verified.ModelSha256, ExpectedDeploymentSha256 = verified.DeploymentSha256,
            DeploymentContractJson = verified.DeploymentJson, ExpectedClassNamesJson = d.GetProperty("names").GetRawText(),
            AIProjectId = p.ProjectId.Length > 0 ? p.ProjectId : verified.ProjectId,
            AIWorkspaceId = p.WorkspaceId.Length > 0 ? p.WorkspaceId : verified.WorkspaceId,
            ModelRecordId = p.ModelRecordId.Length > 0 ? p.ModelRecordId : verified.ModelRecordId,
            Confidence = p.Confidence ?? Number(d, "confidence", .25), Iou = p.Iou ?? Number(post, "per_tile_iou", .45),
            MergeIou = p.MergeIou ?? Number(post, "merge_iou", .5), ClassIds = p.ClassIds,
            Tiled = p.Tiled ?? Boolean(slicing, "tiled", false), TileWidth = p.TileWidth ?? Integer(slicing, "tile_width", 1024),
            TileHeight = p.TileHeight ?? Integer(slicing, "tile_height", 1024), Overlap = p.Overlap ?? Number(slicing, "overlap", .15), BatchSize = p.BatchSize
        };
        var errors = YoloInferenceValidation.Validate(options);
        if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        if (YoloInferenceValidation.ParseClasses(options.ClassIds).Any(id => !verified.Names.ContainsKey(id))) throw new ArgumentException("所选类别不在已验证模型类别表中。");
        using var edit = session.BeginEdit(createNew ? "创建已验证 YOLO 节点" : "应用已验证 YOLO 模型");
        if (createNew) { session.AddNode("YoloInference", 120, 120); selected = session.SelectedNode!; }
        session.CommitParameters(selected!, JsonDefaults.Element(options));
        session.SelectedNode = selected;
        session.SetStatus("模型、部署哈希、类别、预处理及切片参数已写入工程。尚未执行检测，也未发送通信数据。");
        return selected!;
    }
    private static string String(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Number(JsonElement obj, string name, double fallback) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : fallback;
    private static int Integer(JsonElement obj, string name, int fallback) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static bool Boolean(JsonElement obj, string name, bool fallback) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
}

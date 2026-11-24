using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Algorithms.AI;
public enum YoloExecutionMode { Resident, PerRequest }
public enum YoloImageChannel { Png, UncompressedTiff }
public enum YoloQueuePolicy { Wait, Reject }

public sealed record YoloInferenceOptions
{
    [DisplayName("推理生命周期")] public YoloExecutionMode Mode { get; init; } = YoloExecutionMode.Resident;
    [DisplayName("常驻图像通道")] public YoloImageChannel ImageChannel { get; init; } = YoloImageChannel.UncompressedTiff;
    [DisplayName("检测队列满策略（不丢帧）")] public YoloQueuePolicy QueuePolicy { get; init; } = YoloQueuePolicy.Wait;
    [System.Text.Json.Serialization.JsonIgnore, Browsable(false)] public string RunIdentity { get; init; } = "";
    [DisplayName("外部 Python 可执行文件")] public string PythonExecutable { get; init; } = "";
    [DisplayName("已有 YOLOStudio 目录")] public string YoloStudioRoot { get; init; } = "";
    [DisplayName("外部 ONNX 模型")] public string ModelPath { get; init; } = "";
    [DisplayName("推理后端")] public string ExecutionBackend { get; init; } = "onnx-cpu";
    [Browsable(false), DisplayName("固定模型 SHA256")] public string ExpectedModelSha256 { get; init; } = "";
    [Browsable(false), DisplayName("固定部署 SHA256")] public string ExpectedDeploymentSha256 { get; init; } = "";
    [Browsable(false), DisplayName("部署契约快照")] public string DeploymentContractJson { get; init; } = "";
    [Browsable(false), DisplayName("类别表快照")] public string ExpectedClassNamesJson { get; init; } = "";
    [Browsable(false), DisplayName("AI 项目 ID")] public string AIProjectId { get; init; } = "";
    [Browsable(false), DisplayName("AI 工作数据 ID")] public string AIWorkspaceId { get; init; } = "";
    [Browsable(false), DisplayName("模型记录 ID")] public string ModelRecordId { get; init; } = "";
    [DisplayName("使用工程 ROI")] public bool UseRoi { get; init; }
    [DisplayName("ROI X")] public double RoiX { get; init; }
    [DisplayName("ROI Y")] public double RoiY { get; init; }
    [DisplayName("ROI 宽度"), Range(double.Epsilon, 1000000)] public double RoiWidth { get; init; } = 100;
    [DisplayName("ROI 高度"), Range(double.Epsilon, 1000000)] public double RoiHeight { get; init; } = 100;
    [DisplayName("模型输入尺寸"), Range(32, 4096)] public int ImageSize { get; init; } = 640;
    [DisplayName("置信度阈值"), Range(0, 1)] public double Confidence { get; init; } = .25;
    [DisplayName("单片 NMS IoU"), Range(0, 1)] public double Iou { get; init; } = .45;
    [DisplayName("切片合并 IoU"), Range(0, 1)] public double MergeIou { get; init; } = .5;
    [DisplayName("保留类别 ID（逗号分隔）")] public string ClassIds { get; init; } = "";
    [DisplayName("使用已有 Tile 推理")] public bool Tiled { get; init; }
    [DisplayName("切片宽度"), Range(16, 8192)] public int TileWidth { get; init; } = 1024;
    [DisplayName("切片高度"), Range(16, 8192)] public int TileHeight { get; init; } = 1024;
    [DisplayName("切片重叠比例"), Range(0, .999999)] public double Overlap { get; init; } = .15;
    [DisplayName("推理批量"), Range(1, 64)] public int BatchSize { get; init; } = 1;
    [DisplayName("每片最大检测数"), Range(1, 10000)] public int MaxDetections { get; init; } = 300;
    [DisplayName("超时 ms"), Range(1, 3600000)] public int TimeoutMilliseconds { get; init; } = 60000;
}

/// <summary>No WPF dependency. Inputs are borrowed; implementations never release their caller's image.</summary>
public interface IYoloInference
{
    ValueTask<YoloInferenceResult> InferAsync(ImageFrame image, YoloInferenceOptions options,
        RotatedRectRoi? roi = null, CancellationToken cancellationToken = default);
}

public static class YoloInferenceValidation
{
    /// <summary>Blank paths are valid editor drafts. Execution validates actual external resources.</summary>
    public static IReadOnlyList<string> Validate(YoloInferenceOptions p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors = new List<string>();
        if (!Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.ImageChannel) || !Enum.IsDefined(p.QueuePolicy)) errors.Add("Invalid lifecycle/image channel/queue policy.");
        if (p.ExecutionBackend != "onnx-cpu") errors.Add("This node supports only explicit onnx-cpu; PT/CUDA testing belongs to the AI workspace.");
        foreach (var digest in new[] { p.ExpectedModelSha256, p.ExpectedDeploymentSha256 })
            if (digest.Length != 0 && (digest.Length != 64 || !digest.All(Uri.IsHexDigit))) errors.Add("Pinned SHA256 must contain exactly 64 hexadecimal characters.");
        if (p.DeploymentContractJson.Length > 262144 || p.ExpectedClassNamesJson.Length > 262144) errors.Add("Deployment/class snapshots exceed 256 KiB.");
        if (!new[] { p.RoiX, p.RoiY, p.RoiWidth, p.RoiHeight }.All(double.IsFinite) || p.RoiWidth <= 0 || p.RoiHeight <= 0)
            errors.Add("ROI coordinates must be finite and dimensions positive.");
        if (p.ImageSize is < 32 or > 4096 || p.ImageSize % 32 != 0) errors.Add("ImageSize must be a multiple of 32 in [32,4096].");
        foreach (var (name, value) in new[] { ("Confidence", p.Confidence), ("Iou", p.Iou), ("MergeIou", p.MergeIou) })
            if (!double.IsFinite(value) || value < 0 || value > 1) errors.Add(name + " must be finite and in [0,1].");
        if (p.TileWidth is < 16 or > 8192 || p.TileHeight is < 16 or > 8192) errors.Add("Tile dimensions must be in [16,8192].");
        if (!double.IsFinite(p.Overlap) || p.Overlap < 0 || p.Overlap >= 1) errors.Add("Overlap must be finite and in [0,1).");
        if (p.BatchSize is < 1 or > 64) errors.Add("BatchSize must be in [1,64].");
        if (p.MaxDetections is < 1 or > 10000) errors.Add("MaxDetections must be in [1,10000].");
        if (p.TimeoutMilliseconds is < 1 or > 3600000) errors.Add("TimeoutMilliseconds must be in [1,3600000].");
        try { ParseClasses(p.ClassIds); } catch (ArgumentException ex) { errors.Add(ex.Message); }
        return errors;
    }

    public static int[] ParseClasses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var parts = text.Split(',');
        if (parts.Length > 1000 || parts.Any(x => !int.TryParse(x.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0))
            throw new ArgumentException("ClassIds must contain at most 1000 comma-separated nonnegative integer IDs.");
        return parts.Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
    }
}

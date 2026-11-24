using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace VisionPlatform.Algorithms.AI;

/// <summary>A verified local artifact, not a loaded model or a promise about detection accuracy.</summary>
public sealed record VerifiedYoloDeployment(string ModelPath, string ModelSha256, string DeploymentPath,
    string DeploymentSha256, int InputSize, IReadOnlyDictionary<int, string> Names, JsonElement Deployment);

public static class YoloDeploymentVerifier
{
    public static async Task<VerifiedYoloDeployment> VerifyAsync(string modelPath, int? inputSize = null, CancellationToken cancellationToken = default)
    {
        var model = Path.GetFullPath(modelPath);
        if (!File.Exists(model) || !Path.GetExtension(model).Equals(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("应用到视觉流程需要存在的 ONNX 模型；PT/CUDA 测试模型须先显式导出并验证 ONNX。");
        var sidecar = Path.Combine(Path.GetDirectoryName(model)!, "deployment.json");
        if (!File.Exists(sidecar)) throw new FileNotFoundException("模型目录缺少 deployment.json，不能推测预处理或输出布局。", sidecar);
        if (new FileInfo(sidecar).Length > 262144) throw new InvalidDataException("deployment.json exceeds 256 KiB.");
        var modelSha = await DigestAsync(model, cancellationToken).ConfigureAwait(false);
        var sidecarBytes = await File.ReadAllBytesAsync(sidecar, cancellationToken).ConfigureAwait(false);
        var deploymentSha = Convert.ToHexString(SHA256.HashData(sidecarBytes)).ToLowerInvariant();
        using var document = JsonDocument.Parse(sidecarBytes, new JsonDocumentOptions { MaxDepth = 32 });
        var d = document.RootElement;
        try
        {
            Require(d.GetProperty("schema_version").GetInt32() == 1 && d.GetProperty("task").GetString() == "detect", "仅支持 schema1 目标检测部署契约。");
            Require(d.GetProperty("model_file").GetString() == Path.GetFileName(model) &&
                d.GetProperty("onnx_sha256").GetString()?.Equals(modelSha, StringComparison.OrdinalIgnoreCase) == true, "模型文件名或 SHA256 与部署契约不一致。");
            var input = d.GetProperty("input");
            var size = inputSize ?? input.GetProperty("height").GetInt32();
            Require(size is >= 32 and <= 4096 && size % 32 == 0, "模型输入尺寸须为32的倍数，范围32..4096。");
            Require(input.GetProperty("layout").GetString() == "NCHW" && input.GetProperty("channel_order").GetString() == "RGB" &&
                input.GetProperty("dtype").GetString() is "tensor(float)" or "tensor(float16)", "当前节点仅支持 RGB/NCHW 浮点 ONNX 部署。");
            Require(input.GetProperty("dynamic").GetBoolean() || input.GetProperty("height").GetInt32() == size && input.GetProperty("width").GetInt32() == size,
                "输入尺寸与静态 ONNX 部署尺寸不一致。");
            var normalization = input.GetProperty("normalization");
            Require(Math.Abs(normalization.GetProperty("scale").GetDouble() - 1d / 255) < 1e-15 &&
                ArrayEquals(normalization.GetProperty("mean"), 0) && ArrayEquals(normalization.GetProperty("std"), 1), "归一化契约与现有 YOLOStudio ONNX 后端不兼容。");
            var resize = input.GetProperty("resize");
            Require(resize.GetProperty("method").GetString() == "letterbox" && resize.GetProperty("interpolation").GetString() == "bilinear" &&
                resize.GetProperty("keep_aspect_ratio").GetBoolean() && resize.GetProperty("scaleup").GetBoolean() &&
                !resize.GetProperty("auto").GetBoolean() && resize.GetProperty("center").GetBoolean() && ArrayEquals(resize.GetProperty("padding_value"), 114),
                "Letterbox 契约与现有后端不一致，不能静默替换缩放/填充方式。");
            Require(d.GetProperty("output").GetProperty("format").GetString() is "xyxy_conf_class" or "xywh_class_scores", "未支持的 ONNX 输出布局。");
            var names = d.GetProperty("names").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetString() ?? "");
            Require(names.Count is >= 1 and <= 10000 && names.Keys.Order().SequenceEqual(Enumerable.Range(0, names.Count)) &&
                names.Values.All(n => n.Length is >= 1 and <= 1024 && !n.Any(char.IsControl)), "类别 ID/名称表不完整或不合法。");
            var preprocessing = d.GetProperty("preprocessing");
            Require(preprocessing.GetProperty("preview_affects_inference").ValueKind == JsonValueKind.False, "显示效果不得参与模型预处理。");
            var config = preprocessing.GetProperty("config");
            var low = config.GetProperty("low").GetDouble(); var high = config.GetProperty("high").GetDouble();
            var plow = config.GetProperty("percentile_low").GetDouble(); var phigh = config.GetProperty("percentile_high").GetDouble();
            var gamma = config.GetProperty("gamma").GetDouble();
            Require(config.GetProperty("mode").GetString() is "fixed" or "percentile" &&
                new[] { low, high, plow, phigh, gamma }.All(double.IsFinite) && low < high && plow >= 0 && plow < phigh && phigh <= 100 && gamma is >= .1 and <= 10,
                "部署的16位灰度映射快照不合法。");
            var post = d.GetProperty("postprocess");
            Require(post.GetProperty("class_aware").GetBoolean() && post.GetProperty("merge_method").GetString() == "IoU NMS", "后处理契约与现有跨片类别NMS不一致。");
            return new(model, modelSha, sidecar, deploymentSha, size, new ReadOnlyDictionary<int, string>(names), d.Clone());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("deployment.json 缺少或包含无效的模型、预处理及类别字段。", ex); }
    }
    private static bool ArrayEquals(JsonElement array, double expected) => array.ValueKind == JsonValueKind.Array &&
        array.GetArrayLength() == 3 && array.EnumerateArray().All(v => Math.Abs(v.GetDouble() - expected) < 1e-15);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    public static async Task<string> DigestAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}

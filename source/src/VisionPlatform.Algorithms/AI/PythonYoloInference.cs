using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Imaging;
using VisionPlatform.Contracts.Deployment;

namespace VisionPlatform.Algorithms.AI;

/// <summary>Transport seam for portable tests; production always invokes the fixed audited bridge.</summary>
public interface IYoloBridgeRunner
{
    Task<string> RunAsync(string pythonExecutable, string bridgeScript, string requestPath,
        int timeoutMilliseconds, CancellationToken cancellationToken);
}

public sealed class PythonYoloInference : IYoloInference
{
    private readonly IYoloBridgeRunner _runner;
    private readonly string _bridgeScript;
    public PythonYoloInference(IYoloBridgeRunner? runner = null, string? bridgeScript = null)
    {
        _runner = runner ?? new PythonYoloBridgeRunner();
        _bridgeScript = bridgeScript ?? Path.Combine(AppContext.BaseDirectory, "tools", "yolo_bridge", "yolo_bridge.py");
    }

    public async ValueTask<YoloInferenceResult> InferAsync(ImageFrame image, YoloInferenceOptions options,
        RotatedRectRoi? roi = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(options);
        var total = Stopwatch.StartNew();
        MachineConfiguration? machineConfiguration = null;
        // Legacy explicit paths remain readable. New portable nodes leave machine fields empty.
        if (string.IsNullOrWhiteSpace(options.PythonExecutable) || string.IsNullOrWhiteSpace(options.YoloStudioRoot))
        {
            var machine = new MachineConfigurationStore().Load();
            machineConfiguration = machine;
            options = options with { PythonExecutable = string.IsNullOrWhiteSpace(options.PythonExecutable) ? machine.DetectionPython : options.PythonExecutable,
                YoloStudioRoot = string.IsNullOrWhiteSpace(options.YoloStudioRoot) ? machine.DetectionComponentRoot : options.YoloStudioRoot };
        }
        cancellationToken.ThrowIfCancellationRequested();
        var errors = YoloInferenceValidation.Validate(options);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        if (image.PixelFormat.IsBayer()) throw new ArgumentException("Bayer input requires an explicit PixelFormatConvert node to RGB8/BGR8 before YOLO inference.");
        var python = RequiredFile(options.PythonExecutable, "Configure an existing PythonExecutable; no environment is installed automatically.");
        if (string.IsNullOrWhiteSpace(options.YoloStudioRoot) || !Directory.Exists(options.YoloStudioRoot))
            throw new DirectoryNotFoundException("Configure the existing external YoloStudioRoot directory.");
        var root = Path.GetFullPath(options.YoloStudioRoot);
        foreach (var module in new[] { "core/inference/backend.py", "core/inference/config.py", "core/inference/service.py" })
            RequiredFile(Path.Combine(root, module), "The configured YOLOStudio does not expose its audited inference modules.");
        var model = RequiredFile(options.ModelPath, "Configure an existing external ONNX ModelPath; no model is downloaded.");
        if (!string.Equals(Path.GetExtension(model), ".onnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This adapter uses the existing ONNX CPU backend only. PT/CUDA is not silently substituted.");
        RequiredFile(_bridgeScript, "YOLO bridge script is missing from the application deployment.");
        var (sha, classes) = await VerifyDeploymentAsync(model, options, cancellationToken).ConfigureAwait(false);
        using var admission = _runner is ResidentYoloBridge resident ? await resident.AcquireFrameBudgetAsync(options.QueuePolicy, options.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false) : null;
        var requestId = Guid.NewGuid().ToString("N");
        var selected = YoloInferenceValidation.ParseClasses(options.ClassIds);
        if (selected.Any(x => !classes.ContainsKey(x))) throw new ArgumentException("ClassIds contains an ID absent from deployment.json.");
        roi ??= options.UseRoi ? new RectRoi(options.RoiX, options.RoiY, options.RoiWidth, options.RoiHeight).ToRotated(image.CoordinateSpace.Id) : null;
        using var region = RecognitionRegion.Open(image, roi);
        var started = Stopwatch.StartNew();
        var directory = Path.Combine(machineConfiguration?.CacheDirectory ?? Path.GetTempPath(), "VisionPlatform-Yolo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tiff = _runner is ResidentYoloBridge && options.ImageChannel == YoloImageChannel.UncompressedTiff;
            var imagePath = Path.Combine(directory, tiff ? "input.tif" : "input.png");
            var encode = Stopwatch.StartNew();
            await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); if (tiff) ControlledTiffChannel.Write(region, imagePath, cancellationToken); else ImageFile.Save(region, imagePath); }, cancellationToken).ConfigureAwait(false);
            var encodeMs = encode.Elapsed.TotalMilliseconds;
            cancellationToken.ThrowIfCancellationRequested();
            var request = new { schema_version = 1, request_id = requestId, run_id = options.RunIdentity, image_frame_id = image.FrameId.ToString("D"), yolo_root = root, model_path = model, image_path = imagePath,
                image_lease = new { lease_id = requestId, width = region.Width, height = region.Height, channels = region.PixelFormat.BytesPerPixel(), source_stride = region.Stride, stored_stride = region.Width * region.PixelFormat.BytesPerPixel(), payload_length = (long)region.Width * region.Height * region.PixelFormat.BytesPerPixel(), file_length = new FileInfo(imagePath).Length, format = region.PixelFormat == PixelFormat.Mono8 ? "Mono8" : "RGB8" },
                expected_sha256 = sha, image_size = options.ImageSize, confidence = options.Confidence, iou = options.Iou,
                merge_iou = options.MergeIou, class_ids = selected, tiled = options.Tiled, tile_width = options.TileWidth,
                tile_height = options.TileHeight, overlap = options.Overlap, batch = options.BatchSize, max_det = options.MaxDetections };
            var requestPath = Path.Combine(directory, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            var exchange = Stopwatch.StartNew();
            var json = await _runner.RunAsync(python, Path.GetFullPath(_bridgeScript), requestPath,
                options.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var observed = JsonDocument.Parse(json);
            if (observed.RootElement.TryGetProperty("run_id", out var rid) && rid.GetString() != options.RunIdentity) throw new InvalidDataException("Inference run identity mismatch.");
            var timings = observed.RootElement.TryGetProperty("timings", out var t) ? t.Deserialize<Dictionary<string, double>>()! : new();
            timings["image_encode_write_ms"] = encodeMs; timings["transport_and_python_ms"] = exchange.Elapsed.TotalMilliseconds; timings["node_algorithm_total_ms"] = total.Elapsed.TotalMilliseconds; timings["channel_file_bytes"] = new FileInfo(imagePath).Length;
            return ParseResponse(json, region, sha, classes, selected, started.Elapsed, requestId, image.FrameId) with
            { FrameId = image.FrameId, TaskId = requestId, AIProjectId = options.AIProjectId,
              AIWorkspaceId = options.AIWorkspaceId, ModelRecordId = options.ModelRecordId, Timings = new System.Collections.ObjectModel.ReadOnlyDictionary<string, double>(timings) };
        }
        finally
        {
            // Only the GUID-named directory created above belongs to this operation.
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RequiredFile(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException(message, path);
        return Path.GetFullPath(path);
    }

    private static async Task<(string Sha, Dictionary<int, string> Names)> VerifyDeploymentAsync(string model,
        YoloInferenceOptions options, CancellationToken ct)
    {
        var sidecar = RequiredFile(Path.Combine(Path.GetDirectoryName(model)!, "deployment.json"), "Model deployment.json is missing; preprocessing/output layout cannot be inferred safely.");
        if (new FileInfo(sidecar).Length > 262144) throw new InvalidDataException("deployment.json exceeds 256 KiB.");
        await using var file = File.OpenRead(model);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
        if (options.ExpectedModelSha256.Length != 0 && !sha.Equals(options.ExpectedModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected model changed since it was applied to this node; re-verify and apply explicitly.");
        if (options.ExpectedDeploymentSha256.Length != 0)
        {
            await using var deploymentFile = File.OpenRead(sidecar);
            var deploymentSha = Convert.ToHexString(await SHA256.HashDataAsync(deploymentFile, ct).ConfigureAwait(false));
            if (!deploymentSha.Equals(options.ExpectedDeploymentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("deployment.json changed since it was applied to this node; no model or preprocessing is silently replaced.");
        }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, ct).ConfigureAwait(false));
        var d = json.RootElement;
        if (options.DeploymentContractJson.Length != 0 && !System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(options.DeploymentContractJson), System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText())))
            throw new InvalidDataException("Current deployment does not match the formal node's preprocessing/deployment snapshot.");
        if (d.GetProperty("schema_version").GetInt32() != 1 || d.GetProperty("task").GetString() != "detect" ||
            d.GetProperty("model_file").GetString() != Path.GetFileName(model) ||
            !string.Equals(d.GetProperty("onnx_sha256").GetString(), sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model SHA256/task/schema does not match deployment.json.");
        var input = d.GetProperty("input");
        if (input.GetProperty("layout").GetString() != "NCHW" || input.GetProperty("channel_order").GetString() != "RGB" ||
            input.GetProperty("dtype").GetString() is not ("tensor(float)" or "tensor(float16)"))
            throw new InvalidDataException("Unsupported deployment input layout/channel order/type.");
        if (!input.GetProperty("dynamic").GetBoolean() && (input.GetProperty("height").GetInt32() != options.ImageSize || input.GetProperty("width").GetInt32() != options.ImageSize))
            throw new ArgumentException("ImageSize must match the static ONNX deployment dimensions.");
        if (d.GetProperty("output").GetProperty("format").GetString() is not ("xyxy_conf_class" or "xywh_class_scores"))
            throw new InvalidDataException("Unsupported ONNX detection output format.");
        var names = d.GetProperty("names").EnumerateObject().ToDictionary(x => int.Parse(x.Name, System.Globalization.CultureInfo.InvariantCulture), x => x.Value.GetString() ?? "");
        if (names.Count is < 1 or > 10000 || !names.Keys.Order().SequenceEqual(Enumerable.Range(0, names.Count)) || names.Values.Any(x => x.Length > 1024))
            throw new InvalidDataException("Invalid deployment class names/IDs.");
        if (options.ExpectedClassNamesJson.Length != 0 && !System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(options.ExpectedClassNamesJson), System.Text.Json.Nodes.JsonNode.Parse(d.GetProperty("names").GetRawText())))
            throw new InvalidDataException("Current model class names differ from the formal node's class snapshot.");
        return (sha, names);
    }

    private static YoloInferenceResult ParseResponse(string text, ImageFrame region, string sha,
        Dictionary<int, string> names, int[] selected, TimeSpan elapsed, string requestId, Guid frameId)
    {
        if (text.Length > PythonYoloBridgeRunner.MaximumOutputCharacters) throw new InvalidDataException("YOLO response exceeds the output budget.");
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var r = json.RootElement;
        if (r.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("Unsupported bridge response schema.");
        if (!r.GetProperty("success").GetBoolean()) throw new InvalidOperationException("YOLO inference failed: " + r.GetProperty("error").GetString());
        if (r.TryGetProperty("request_id", out var task) && task.GetString() != requestId ||
            r.TryGetProperty("image_frame_id", out var frame) && frame.GetString() != frameId.ToString("D"))
            throw new InvalidDataException("YOLO response belongs to another inference request or frame.");
        if (r.GetProperty("width").GetInt32() != region.Width || r.GetProperty("height").GetInt32() != region.Height ||
            !string.Equals(r.GetProperty("model_sha256").GetString(), sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("YOLO response image/model identity mismatch.");
        var provider = r.GetProperty("provider").GetString();
        var tiles = r.GetProperty("tile_count").GetInt32(); var forward = r.GetProperty("inference_ms").GetDouble();
        if (provider != "ONNX Runtime CPUExecutionProvider" || tiles is < 1 or > 4096 || !double.IsFinite(forward) || forward < 0)
            throw new InvalidDataException("YOLO provider/timing/tile metadata is invalid.");
        var array = r.GetProperty("boxes");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 10000) throw new InvalidDataException("YOLO detections exceed the result budget.");
        var detections = new List<YoloDetection>(); var overlays = new List<RecognitionOverlay>();
        foreach (var box in array.EnumerateArray())
        {
            var id = box.GetProperty("class_id").GetInt32(); var confidence = box.GetProperty("confidence").GetDouble();
            var x = box.GetProperty("x").GetDouble(); var y = box.GetProperty("y").GetDouble();
            var width = box.GetProperty("w").GetDouble(); var height = box.GetProperty("h").GetDouble();
            if (!names.TryGetValue(id, out var label) || !new[] { confidence, x, y, width, height }.All(double.IsFinite) ||
                confidence < 0 || confidence > 1 || x < 0 || y < 0 || width <= 0 || height <= 0 ||
                x + width > region.Width + 1e-4 || y + height > region.Height + 1e-4 || (selected.Length > 0 && !selected.Contains(id)))
                throw new InvalidDataException("YOLO returned invalid class/score/original-image bounds.");
            var bounds = new RectRoi(x + region.CoordinateSpace.OriginX, y + region.CoordinateSpace.OriginY, width, height);
            detections.Add(new(id, label, confidence, bounds));
            overlays.Add(new(RecognitionOverlayKind.Polygon, $"{label} {confidence:F3}",
                [new(bounds.X, bounds.Y), new(bounds.X + width, bounds.Y), new(bounds.X + width, bounds.Y + height), new(bounds.X, bounds.Y + height)], region.CoordinateSpace.Id));
        }
        return new(true, detections.AsReadOnly(), region.CoordinateSpace.Id, elapsed, overlays.AsReadOnly(), sha, provider!, tiles, forward);
    }
}

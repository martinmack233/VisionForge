using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.AI;
using VisionPlatform.AI.Deployment;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Hosting;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

if (args.Length != 5) { Console.Error.WriteLine("AiDeploymentAcceptance <VisionPlatform root> <YOLOStudio root> <existing ONNX> <existing image> <new evidence dir>"); return 2; }
var root = Path.GetFullPath(args[0]); var yoloRoot = Path.GetFullPath(args[1]);
var sourceModel = Path.GetFullPath(args[2]); var sourceImage = Path.GetFullPath(args[3]); var evidence = Path.GetFullPath(args[4]);
Directory.CreateDirectory(evidence);
var checks = new Dictionary<string, bool>(); var cases = new List<object>();
var baselineBytes = FrameHandle.ActiveBytes;
try
{
    var originalHashes = new[] { sourceModel, Path.Combine(Path.GetDirectoryName(sourceModel)!, "deployment.json"), sourceImage }
        .ToDictionary(p => p, p => YoloDeploymentVerifier.DigestAsync(p).GetAwaiter().GetResult());
    var fixture = Path.Combine(evidence, "fixture"); Directory.CreateDirectory(fixture);
    var model = Path.Combine(fixture, Path.GetFileName(sourceModel));
    File.Copy(sourceModel, model, false); File.Copy(Path.Combine(Path.GetDirectoryName(sourceModel)!, "deployment.json"), Path.Combine(fixture, "deployment.json"), false);
    var imagePath = Path.Combine(fixture, "input.png");
    using (var decoded = ImageFile.Load(sourceImage)) ImageFile.Save(decoded, imagePath);
    var settings = new AiServiceSettings { YoloRoot = yoloRoot, PythonPath = Path.Combine(yoloRoot, ".venv", "Scripts", "python.exe"),
        DataHome = Path.Combine(evidence, "service-data"), TransferRoot = Path.Combine(evidence, "transfer"),
        ServiceScript = Path.Combine(root, "tools", "ai_workspace_service", "service.py"), RequestTimeoutMilliseconds = 90000 };
    await using var client = new AiWorkspaceClient();
    await client.StartAsync(settings);
    var project = await client.CallAsync("projects.create", new { name = "部署隔离验收" });
    var aiProjectId = project.GetProperty("project").GetProperty("id").GetString()!;
    await client.CallAsync("context.select", new { project_id = aiProjectId });
    var verified = await AiModelDeployment.VerifyAsync(client, model, 128, aiProjectId);
    checks["real_service_and_local_deployment_verified"] = true;

    using var editor = new EditorSession();
    await editor.ImportImageAsync(imagePath);
    var input = editor.Nodes.Single(n => n.TypeId == "ImageInput");
    var yolo = AiModelDeployment.ApplyToNode(editor, verified, settings, new() { ProjectId = aiProjectId, WorkspaceId = verified.WorkspaceId,
        Confidence = .25, Tiled = false }, createNew: true);
    editor.SetAiProjectLink(new(aiProjectId, verified.WorkspaceId, settings.DataHome));
    Connect(input, "Image", yolo, "Image");
    editor.AddNode("YoloDetectionRoi"); var roiNode = editor.SelectedNode!; Connect(yolo, "Result", roiNode, "Result");
    editor.AddNode("BlobDetection"); var blob = editor.SelectedNode!; Connect(input, "Image", blob, "Image"); Connect(roiNode, "ROI", blob, "ROI");
    await editor.RunAsync(); Require(editor.Results.All(r => r.Status == "Success"), editor.StatusMessage);
    var result = (YoloInferenceResult)editor.CurrentOutputs[yolo.NodeId]["Result"];
    Require(result.Detections.Count > 0, "Real model returned no targets; nonempty ROI/Blob validation cannot be claimed.");
    Require(result.FrameId == ((ImageFrame)editor.CurrentOutputs[input.NodeId]["Image"]).FrameId && result.RunId != Guid.Empty && result.TaskId.Length > 0,
        "Current frame/task/run identity was not preserved.");
    checks["real_ai_target_controls_blob_roi"] = editor.CurrentOutputs[roiNode.NodeId]["ROI"] is RotatedRectRoi && editor.CurrentOutputs[blob.NodeId].ContainsKey("BlobResults");
    var saved = Path.Combine(evidence, "runtime-project.json");
    await editor.SaveAsync(saved);
    var first = result.Detections.ToArray(); await editor.OpenAsync(saved); await editor.RunAsync();
    var reloaded = (YoloInferenceResult)editor.CurrentOutputs[yolo.NodeId]["Result"];
    Require(first.SequenceEqual(reloaded.Detections), "Saved/reopened model deployment produced different detections.");
    checks["formal_project_roundtrip_model_deployment_and_identity"] = editor.SnapshotProject().AiProject?.ProjectId == aiProjectId;
    var plainOptions = editor.Nodes.Single(n => n.NodeId == yolo.NodeId).Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options)!;
    using (var image = ImageFile.Load(imagePath))
    {
        foreach (var mode in new[] { "plain", "tiled", "roi" })
        {
            var options = plainOptions with { Tiled = mode == "tiled", TileWidth = 320, TileHeight = 320, Overlap = .15,
                UseRoi = mode == "roi", RoiX = 20, RoiY = 15, RoiWidth = image.Width - 40, RoiHeight = image.Height - 30 };
            using var directInput = mode == "roi" ? image.CreateView(20, 15, image.Width - 40, image.Height - 30) : image.Share();
            var nativeInput = Path.Combine(evidence, mode + "-input.png"); ImageFile.Save(directInput, nativeInput);
            var request = Path.Combine(evidence, mode + "-native-request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { yolo_root = yoloRoot, model_path = model, image_path = nativeInput, options }, JsonDefaults.Options));
            var native = await Process(settings.PythonPath, ["-B", "-u", Path.Combine(root, "tools", "AiDeploymentAcceptance", "direct_yolo.py"), request], settings.DataHome);
            Require(native.Exit == 0, native.Error); using var nativeResult = JsonDocument.Parse(native.Output);
            var actual = await new PythonYoloInference().InferAsync(image, options);
            var differences = Compare(nativeResult.RootElement.GetProperty("boxes"), actual, mode == "roi" ? 20 : 0, mode == "roi" ? 15 : 0);
            Require(actual.FrameId == image.FrameId && actual.TaskId.Length > 0, "Inference frame/request identity missing.");
            await File.WriteAllTextAsync(Path.Combine(evidence, mode + "-original.json"), native.Output);
            await File.WriteAllTextAsync(Path.Combine(evidence, mode + "-integrated.json"), JsonSerializer.Serialize(actual, JsonDefaults.Options));
            cases.Add(new { Mode = mode, actual.TileCount, Count = actual.Detections.Count, MaximumCoordinateError = differences.Coordinates,
                MaximumConfidenceError = differences.Confidence, actual.ExecutionTime, actual.ForwardMilliseconds, actual.FrameId, actual.TaskId, actual.ModelSha256 });
        }
    }
    checks["original_detector_parity_plain_tiled_roi"] = true;
    await editor.CloseAsync();
    var consolePath = Path.Combine(root, "src", "VisionPlatform.Console", "bin", "Release", "net10.0", "VisionPlatform.Console.exe");
    var consoleOut = Path.Combine(evidence, "console");
    var console = await Process(consolePath, ["run", saved, "--outputs", consoleOut], evidence);
    await File.WriteAllTextAsync(Path.Combine(evidence, "console.log"), console.Output + console.Error);
    Require(console.Exit == 0, "Console failed: " + console.Error);
    using var consoleResult = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(consoleOut, "results.json")));
    var consoleBoxes = consoleResult.RootElement.GetProperty("Nodes").GetProperty(yolo.NodeId).GetProperty("Outputs").GetProperty("Result").GetProperty("Detections");
    Require(consoleBoxes.GetArrayLength() == first.Length, "Console count differs from editor runtime.");
    checks["console_uses_saved_deployment"] = true;
    await using (var host = new RuntimeHostSession(BuiltinRegistration.CreateRegistry()))
    {
        var load = await host.HandleAsync(Request("load", new LoadProjectCommand(saved))); Require(load.Success, load.Message);
        var start = await host.HandleAsync(Request("run", new RunProjectCommand())); Require(start.Success, start.Message);
        var accepted = start.Payload!.Value.Deserialize<HostRunAccepted>(JsonDefaults.Options)!;
        HostResponse response;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            response = await host.HandleAsync(Request("result", new RunReference(accepted.RunId)));
            if (response.Success) break;
            Require(response.ErrorCode == HostErrorCodes.NotReady && deadline.Elapsed < TimeSpan.FromSeconds(90), response.Message);
            await Task.Delay(30);
        }
        var run = response.Payload!.Value.Deserialize<HostResultSnapshot>(JsonDefaults.Options)!;
        Require(run.Success, run.ErrorMessage ?? "Host execution failed.");
        var hostResult = run.Nodes.Single(n => n.NodeId == yolo.NodeId).Outputs["Result"].Value;
        Require(hostResult.GetProperty("Detections").GetArrayLength() == first.Length, "Host count differs from editor.");
        await File.WriteAllTextAsync(Path.Combine(evidence, "host-result.json"), JsonSerializer.Serialize(run, JsonDefaults.Options));
        checks["runtime_host_uses_saved_deployment"] = true;
    }
    foreach (var hash in originalHashes) Require(await YoloDeploymentVerifier.DigestAsync(hash.Key) == hash.Value, "Original artifact was changed.");
    checks["original_model_deployment_and_image_unchanged"] = true;
    Require(FrameHandle.ActiveBytes == baselineBytes, "Acceptance left owned frame buffers alive.");
    checks["frame_ownership_released"] = true;
    await Save(new { Passed = true, Checks = checks, Cases = cases, Project = saved, YoloNodeId = yolo.NodeId,
        ModelSha256 = verified.ModelSha256, DeploymentSha256 = verified.DeploymentSha256, AIProjectId = aiProjectId, AIWorkspaceId = verified.WorkspaceId,
        BuffersBefore = baselineBytes, BuffersAfter = FrameHandle.ActiveBytes, OriginalArtifactsUnchanged = originalHashes,
        RealOnnxCpuTested = true, PtCudaNodeImplemented = false, IndustrialAccuracyValidated = false });
    return 0;

    void Connect(VisionPlatform.Editor.Core.Models.EditorNode source, string output, VisionPlatform.Editor.Core.Models.EditorNode target, string port)
    { Require(editor.TryConnect(source.Outputs.Single(p => p.Name == output), target.Inputs.Single(p => p.Name == port), out var error), error ?? "Connection failed."); }
}
catch (Exception error)
{
    await Save(new { Passed = false, Checks = checks, Cases = cases, Error = error.ToString(), BuffersBefore = baselineBytes, BuffersAfter = FrameHandle.ActiveBytes });
    Console.Error.WriteLine(error); return 1;
}

async Task Save(object report) { var text = JsonSerializer.Serialize(report, JsonDefaults.Options); await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), text); Console.WriteLine(text); }
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static HostRequest Request(string operation, object payload) => new() { RequestId = Guid.NewGuid().ToString("N"), Operation = operation, Payload = JsonDefaults.Element(payload) };
static (double Coordinates, double Confidence) Compare(JsonElement expected, YoloInferenceResult actual, int offsetX, int offsetY)
{
    var boxes = expected.EnumerateArray().ToArray(); Require(boxes.Length == actual.Detections.Count, "Original/integrated detection counts differ.");
    double coordinates = 0, confidence = 0;
    for (var i = 0; i < boxes.Length; i++)
    {
        var a = boxes[i]; var b = actual.Detections[i]; Require(a.GetProperty("class_id").GetInt32() == b.ClassId, "Original/integrated class differs.");
        coordinates = Math.Max(coordinates, new[] { Math.Abs(a.GetProperty("x").GetDouble() + offsetX - b.Bounds.X), Math.Abs(a.GetProperty("y").GetDouble() + offsetY - b.Bounds.Y),
            Math.Abs(a.GetProperty("w").GetDouble() - b.Bounds.Width), Math.Abs(a.GetProperty("h").GetDouble() - b.Bounds.Height) }.Max());
        confidence = Math.Max(confidence, Math.Abs(a.GetProperty("confidence").GetDouble() - b.Confidence));
    }
    Require(coordinates < 1e-4 && confidence < 1e-6, "Original/integrated coordinate or confidence mismatch."); return (coordinates, confidence);
}
static async Task<(int Exit, string Output, string Error)> Process(string executable, IReadOnlyList<string> arguments, string home)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = home };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["YOLO_AUTOINSTALL"] = "false"; start.Environment["PYTHONUTF8"] = "1"; start.Environment["PYTHONIOENCODING"] = "utf-8";
    start.Environment["YOLO_CONFIG_DIR"] = Path.Combine(home, "config", "ultralytics");
    using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Unable to start explicit acceptance process.");
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
    return (process.ExitCode, await output, await error);
}

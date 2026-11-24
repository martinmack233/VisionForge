using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.AI;
using VisionPlatform.AI.Deployment;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Imaging;
using Xunit;

namespace VisionPlatform.AI.Tests;

/// <summary>Deployment authoring/transport contract tests; the tiny byte artifact is not a real ONNX model.</summary>
public sealed class DeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionStudio-AI-Deploy-" + Guid.NewGuid().ToString("N"));
    private string Model => Path.Combine(_root, "model.onnx");
    private string Deployment => Path.Combine(_root, "deployment.json");
    private string Python => Path.Combine(_root, "python.exe");
    public DeploymentTests()
    {
        Directory.CreateDirectory(_root); File.WriteAllBytes(Model, [1, 2, 3]); File.WriteAllBytes(Python, []);
        foreach (var module in new[] { "backend.py", "service.py", "config.py" })
        { var directory = Path.Combine(_root, "core", "inference"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, module), "# contract fixture; never executed"); }
        File.WriteAllText(Path.Combine(_root, "bridge.py"), "# contract fixture");
        WriteDeployment();
    }
    private void WriteDeployment() => File.WriteAllText(Deployment, JsonSerializer.Serialize(new
    {
        schema_version = 1, task = "detect", model_file = "model.onnx", onnx_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Model))).ToLowerInvariant(),
        names = new Dictionary<string, string> { ["0"] = "scratch" },
        input = new { height = 128, width = 128, dynamic = false, layout = "NCHW", channel_order = "RGB", dtype = "tensor(float)",
            normalization = new { scale = 1d / 255, mean = new[] { 0, 0, 0 }, std = new[] { 1, 1, 1 } },
            resize = new { method = "letterbox", interpolation = "bilinear", keep_aspect_ratio = true, scaleup = true, auto = false, center = true, padding_value = new[] { 114, 114, 114 } } },
        output = new { format = "xyxy_conf_class" }, preprocessing = new { preview_affects_inference = false,
            config = new { mode = "fixed", low = 0, high = 65535, percentile_low = 1, percentile_high = 99, gamma = 1 } },
        slicing = new { tiled = true, tile_width = 128, tile_height = 128, overlap = .2 }, confidence = .25,
        postprocess = new { class_aware = true, merge_method = "IoU NMS", per_tile_iou = .45, merge_iou = .5 }
    }));
    private AiServiceSettings Settings => new() { YoloRoot = _root, PythonPath = Python, DataHome = Path.Combine(_root, "home") };

    [Fact]
    public async Task ApplyWritesCompletePinnedContractAndPreservesRoiWithoutExecutingOrConnecting()
    {
        await using var client = new Client(Settings); var verified = await AiModelDeployment.VerifyAsync(client, Model);
        using var session = new EditorSession(); session.AddNode("YoloInference"); var node = session.SelectedNode!;
        session.CommitParameters(node, JsonDefaults.Element(new YoloInferenceOptions { UseRoi = true, RoiX = 5, RoiY = 6, RoiWidth = 8, RoiHeight = 9 }));
        var serviceCalls = client.Calls;
        AiModelDeployment.ApplyToNode(session, verified, Settings, new() { Confidence = .8, ProjectId = "project-id", WorkspaceId = "workspace-id", ModelRecordId = "model-id" });
        Assert.Equal(serviceCalls, client.Calls); Assert.Empty(session.Results); Assert.Empty(session.CurrentOutputs); Assert.Null(session.DisplayImage);
        var p = node.Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options)!;
        Assert.Equal(Model, p.ModelPath); Assert.Equal(verified.ModelSha256, p.ExpectedModelSha256); Assert.Equal(verified.DeploymentSha256, p.ExpectedDeploymentSha256);
        Assert.Equal("onnx-cpu", p.ExecutionBackend); Assert.True(p.Tiled); Assert.Equal(128, p.TileWidth); Assert.Equal(.8, p.Confidence);
        Assert.Equal(128, p.ImageSize); Assert.True(p.UseRoi); Assert.Equal(5, p.RoiX); Assert.Equal("project-id", p.AIProjectId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Deployment)), JsonNode.Parse(p.DeploymentContractJson)));
        session.Undo(); Assert.Equal("", session.SelectedNode!.Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options)!.ModelPath);
        session.Redo(); Assert.Equal(Model, session.SelectedNode!.Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options)!.ModelPath);
    }

    [Fact]
    public async Task ExplicitCreateAddsConfigurationOnlyAndApplyToWrongNodeDoesNotCreateAnything()
    {
        await using var client = new Client(Settings); var verified = await AiModelDeployment.VerifyAsync(client, Model);
        using var session = new EditorSession(); session.AddNode("Threshold");
        Assert.Throws<InvalidOperationException>(() => AiModelDeployment.ApplyToNode(session, verified, Settings)); Assert.Single(session.Nodes);
        var created = AiModelDeployment.ApplyToNode(session, verified, Settings, createNew: true);
        Assert.Equal("YoloInference", created.TypeId); Assert.Equal(2, session.Nodes.Count); Assert.Empty(session.Connections); Assert.Empty(session.Results);
        session.Undo(); Assert.Single(session.Nodes); Assert.Equal("Threshold", session.Nodes[0].TypeId);
    }

    [Fact]
    public async Task InvalidModelPreprocessingOrServiceIdentityCannotBeApplied()
    {
        await using var client = new Client(Settings);
        var pt = Path.Combine(_root, "model.pt"); File.WriteAllBytes(pt, [1]);
        await Assert.ThrowsAsync<ArgumentException>(() => AiModelDeployment.VerifyAsync(client, pt)); Assert.Equal(0, client.Calls);
        var snapshot = JsonNode.Parse(File.ReadAllText(Deployment))!; snapshot["input"]!["normalization"]!["scale"] = .5;
        File.WriteAllText(Deployment, snapshot.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => AiModelDeployment.VerifyAsync(client, Model)); Assert.Equal(0, client.Calls);
        WriteDeployment(); client.WrongModelIdentity = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => AiModelDeployment.VerifyAsync(client, Model));
    }

    [Fact]
    public async Task ModifiedModelOrDeploymentFailsBeforeStartingExternalInference()
    {
        await using var client = new Client(Settings); var verified = await AiModelDeployment.VerifyAsync(client, Model);
        using var editor = new EditorSession();
        var node = AiModelDeployment.ApplyToNode(editor, verified, Settings, createNew: true);
        var p = node.Parameters.Deserialize<YoloInferenceOptions>(JsonDefaults.Options)!;
        using var frame = FrameFactory.CopyFrom(new byte[64], 8, 8, PixelFormat.Mono8);
        var runner = new NeverRunner(); var engine = new PythonYoloInference(runner, Path.Combine(_root, "bridge.py"));
        File.AppendAllText(Deployment, " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.InferAsync(frame, p).AsTask()); Assert.Equal(0, runner.Calls);
        WriteDeployment(); File.WriteAllBytes(Model, [4, 5, 6]); WriteDeployment();
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.InferAsync(frame, p).AsTask()); Assert.Equal(0, runner.Calls);
    }

    private sealed class NeverRunner : IYoloBridgeRunner
    { public int Calls; public Task<string> RunAsync(string pythonExecutable, string bridgeScript, string requestPath, int timeoutMilliseconds, CancellationToken ct) { Calls++; throw new Exception("must never execute"); } }
    private sealed class Client(AiServiceSettings settings) : IAiWorkspaceClient
    {
        public bool IsRunning => true;
        public AiServiceSettings Settings => settings;
        public int Calls;
        public bool WrongModelIdentity;
        public event EventHandler<AiServiceEvent>? EventReceived { add { } remove { } }
        public Task StartAsync(AiServiceSettings value, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken ct = default)
        {
            Assert.Equal("deployment.inspect", method); Calls++;
            var request = JsonSerializer.SerializeToElement(payload); var path = request.GetProperty("model_path").GetString()!;
            var verified = await YoloDeploymentVerifier.VerifyAsync(path, request.GetProperty("imgsz").GetInt32(), ct);
            return JsonSerializer.SerializeToElement(new { validated = true, backend = "onnx", provider = "ONNX Runtime CPUExecutionProvider",
                model_path = verified.ModelPath, model_sha256 = WrongModelIdentity ? new string('0', 64) : verified.ModelSha256,
                deployment_sha256 = verified.DeploymentSha256, deployment = verified.Deployment, input_size = verified.InputSize,
                workspace_id = "workspace-id", project_id = "project-id", model_record_id = "model-id" });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose()
    {
        if (_root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(_root).StartsWith("VisionStudio-AI-Deploy-", StringComparison.Ordinal)) Directory.Delete(_root, true);
    }
}

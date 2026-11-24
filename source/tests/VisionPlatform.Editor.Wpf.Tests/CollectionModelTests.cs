using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.AI;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;
public sealed class CollectionModelTests
{
    [Fact]
    public void ExplicitUseForTestingLocksActualOnnxShapeAndKeepsDeviceAndTrainingDraft() => Sta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "collection-model-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "client-contract-only.onnx"); await File.WriteAllTextAsync(path, "protocol fixture, not executed");
        using var session = new EditorSession(); await using var client = new Client(); await using var vm = new AiWorkspaceViewModel(session, client, new());
        await vm.ConnectAsync(); vm.SelectedProject = vm.Projects[0]; await Task.Delay(30);
        vm.InputSizeText = "320"; vm.BatchText = "8"; vm.TestDevice = "2"; vm.TestBatchText = "8";
        vm.SelectedModel = new("model", "fixture", path, "training-source", "", "", ""); Assert.Equal("", vm.TestModelPath);
        var pageRequested = false; vm.TestPageRequested += () => pageRequested = true;
        await vm.UseSelectedModelForTestAsync(); Assert.True(pageRequested); Assert.Equal(path, vm.TestModelPath); Assert.Equal("onnx", vm.TestBackend); Assert.Equal("2", vm.TestDevice);
        Assert.False(vm.TestBatchEditable); Assert.False(vm.TestSizeEditable); Assert.Equal("1", vm.TestBatchText); Assert.Equal("64", vm.TestInputSizeText);
        Assert.Equal("320", vm.InputSizeText); Assert.Equal("8", vm.BatchText); Assert.DoesNotContain(client.Requests, p => p.Method == "inference.start");
        vm.TestDevice = "cpu"; vm.TestInputsText = Path.Combine(folder, "input.png"); await vm.StartInferenceAsync();
        var request = Assert.Single(client.Requests, p => p.Method == "inference.start"); var config = request.Payload.GetProperty("config"); Assert.Equal(1, config.GetProperty("batch").GetInt32()); Assert.Equal(64, config.GetProperty("imgsz").GetInt32());
        File.Delete(path); await Assert.ThrowsAsync<FileNotFoundException>(() => vm.UseSelectedModelForTestAsync()); Assert.Single(client.Requests, p => p.Method == "inference.start");
    });
    [Fact]
    public void MissingOrRelativeTestModelNeverCreatesInferenceRequest() => Sta(async () =>
    {
        using var session = new EditorSession(); await using var client = new Client(); await using var vm = new AiWorkspaceViewModel(session, client, new()); await vm.ConnectAsync(); vm.SelectedProject = vm.Projects[0]; await Task.Delay(30);
        vm.TestModelPath = "relative.onnx"; await Assert.ThrowsAsync<ArgumentException>(vm.StartInferenceAsync);
        vm.TestModelPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"); await Assert.ThrowsAsync<FileNotFoundException>(vm.StartInferenceAsync);
        Assert.DoesNotContain(client.Requests, p => p.Method == "inference.start");
    });
    private static void Sta(Func<Task> body)
    {
        Exception? error = null; var thread = new Thread(() => { var d = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d)); d.BeginInvoke(new Action(async () => { try { await body(); } catch (Exception ex) { error = ex; } finally { d.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run(); });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30))); if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class Client : IAiWorkspaceClient
    {
        public bool IsRunning { get; private set; } public AiServiceSettings Settings { get; private set; } = new();
        public event EventHandler<AiServiceEvent>? EventReceived { add { } remove { } }
        public List<(string Method, JsonElement Payload)> Requests { get; } = [];
        public Task StartAsync(AiServiceSettings settings, CancellationToken ct = default) { IsRunning = true; Settings = settings; return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken ct = default)
        {
            Requests.Add((method, JsonDefaults.Element(payload ?? new { })));
            object result = method switch { "hello" => new { workspace_id = "isolated" }, "projects.list" => new { items = new[] { new { id = "project", name = "测试上下文" } } },
                "models.input_contract" => new { fixed_batch = 1, fixed_size = 64, shape = new[] { 1, 3, 64, 64 } }, "inference.start" => new { task_id = "protocol-only", status = "recorded" }, _ => new { items = Array.Empty<object>() } };
            return Task.FromResult(JsonDefaults.Element(result));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

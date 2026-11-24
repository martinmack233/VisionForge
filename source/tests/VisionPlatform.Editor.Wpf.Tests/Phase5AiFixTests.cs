using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.AI;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase5AiFixTests
{
    [Fact]
    public void InvalidNewTrainingFieldsNeverSendOrCreateTrainingTasks() => Sta(async () =>
    {
        using var files = new Files(); using var session = new EditorSession(); await using var client = new RecordingClient(files);
        await using var vm = new AiWorkspaceViewModel(session, client, new()); await Initialize(vm);
        vm.TrainingDataset = files.Dataset;
        foreach (var (path, message) in new[] { ("", "请先选择初始权重。"), ("relative.pt", "请选择完整路径"), (files.Root + "/missing.pt", "所选初始权重不存在"), (files.Onnx, "训练初始权重需要 .pt 文件") })
        {
            vm.TrainingModel = path; var error = await Assert.ThrowsAsync<ArgumentException>(() => vm.StartTrainingAsync(false));
            Assert.StartsWith(message, error.Message); Assert.Equal(error.Message, vm.TrainingModelError);
            Assert.DoesNotContain(client.Requests, r => r.Method == "training.start"); Assert.Empty(vm.Tasks);
            vm.TrainingModel = files.Model; Assert.Empty(vm.TrainingModelError);
        }
        vm.TrainingDataset = ""; await Assert.ThrowsAsync<ArgumentException>(() => vm.StartTrainingAsync(false)); Assert.Equal("请先选择已生成的数据集 data.yaml。", vm.TrainingDatasetError);
        vm.TrainingDataset = files.Dataset; Assert.Empty(vm.TrainingDatasetError);
        vm.InputSizeText = "33"; await Assert.ThrowsAsync<ArgumentException>(() => vm.StartTrainingAsync(false)); Assert.Contains("32", vm.TrainingSettingsError);
        Assert.DoesNotContain(client.Requests, r => r.Method == "training.start");
    });

    [Fact]
    public void ValidNewRunNormalizesOnlyOuterQuotesAndResumeUsesOriginalTaskWithoutNewDraft() => Sta(async () =>
    {
        using var files = new Files(); using var session = new EditorSession(); await using var client = new RecordingClient(files);
        await using var vm = new AiWorkspaceViewModel(session, client, new()); await Initialize(vm);
        vm.TrainingModel = " \"" + files.Model + "\" "; vm.TrainingDataset = " '" + files.Dataset + "' "; vm.InputSizeText = "64";
        await vm.StartTrainingAsync(false);
        var request = Assert.Single(client.Requests, r => r.Method == "training.start");
        Assert.Equal(files.Model, request.Payload.GetProperty("config").GetProperty("model").GetString());
        Assert.Equal(files.Dataset, request.Payload.GetProperty("config").GetProperty("dataset").GetString()); Assert.False(request.Payload.GetProperty("config").GetProperty("allow_download").GetBoolean());
        vm.TrainingModel = ""; vm.TrainingDataset = ""; vm.EpochsText = "invalid-new-draft";
        await vm.StartTrainingAsync(true);
        var resume = Assert.Single(client.Requests, r => r.Method == "training.resume");
        Assert.Equal("original-task", resume.Payload.GetProperty("task_id").GetString()); Assert.False(resume.Payload.TryGetProperty("config", out _));
        Assert.Empty(vm.TrainingModelError);
    });

    [Fact]
    public void AnnotationFormattingAndFailedEditsNeverRoundOrOverwriteOriginalValues() => Sta(async () =>
    {
        using var files = new Files(); using var session = new EditorSession(); await using var client = new RecordingClient(files);
        await using var vm = new AiWorkspaceViewModel(session, client, new()); await Initialize(vm); vm.SelectedImage = vm.Images[0];
        var box = Assert.Single(vm.Boxes); vm.SelectedBox = box;
        var x = box.X; var y = box.Y; var width = box.Width; var height = box.Height;
        Assert.Equal("12.123", vm.AnnotationDraft.XText); Assert.Equal("1. 测试斑块", box.DisplayName);
        vm.ApplyAnnotationCoordinates(); await vm.FlushAnnotationsAsync();
        Assert.Equal(x, box.X); Assert.Equal(y, box.Y); Assert.Equal(width, box.Width); Assert.Equal(height, box.Height);
        Assert.DoesNotContain(client.Requests, r => r.Method == "annotations.save");
        vm.AnnotationDraft.XText = "bad-value"; vm.ApplyAnnotationCoordinates();
        Assert.Equal("bad-value", vm.AnnotationDraft.XText); Assert.Contains("X", vm.AnnotationEditError); Assert.Equal(x, box.X);
        vm.AnnotationDraft.XText = "15.25"; vm.ApplyAnnotationCoordinates(); await vm.FlushAnnotationsAsync();
        Assert.Equal(15.25, box.X); Assert.Equal(y, box.Y); Assert.Equal(width, box.Width); Assert.Equal(height, box.Height);
        var saved = Assert.Single(client.Requests, r => r.Method == "annotations.save").Payload.GetProperty("boxes")[0];
        Assert.Equal(y, saved.GetProperty("y").GetDouble()); Assert.Equal(box.Id, saved.GetProperty("id").GetString());
        vm.UndoAnnotation(); Assert.Equal(x, Assert.Single(vm.Boxes).X); await vm.FlushAnnotationsAsync();
    });

    [Fact]
    public void MissingModelFocusAndAnnotationSidebarUseActualWpfControls() => Sta(async () =>
    {
        using var files = new Files(); using var session = new EditorSession(); await using var client = new RecordingClient(files);
        var page = new AiWorkspacePage(session, client, new()); var vm = page.ViewModel;
        var window = new Window { Content = page, Width = 1100, Height = 720, ShowInTaskbar = false, Left = -22000, Top = -22000 };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        try
        {
            window.Show(); await Initialize(vm); vm.TrainingDataset = files.Dataset;
            await vm.RunUiAsync(() => vm.StartTrainingAsync(false)); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var input = Assert.IsType<TextBox>(page.FindName("TrainingModelInput")); Assert.True(input.IsKeyboardFocused || ReferenceEquals(FocusManager.GetFocusedElement(window), input));
            Assert.Equal("请先选择初始权重。", vm.TrainingModelError); Assert.Equal(3, ((TabControl)page.FindName("AiTabs")).SelectedIndex);
            Assert.Empty(vm.Tasks); Assert.DoesNotContain(client.Requests, r => r.Method == "training.start");
            Assert.Equal(new Thickness(14), ((ScrollViewer)page.FindName("AnnotationPropertiesScroll")).Padding);
            Assert.False(vm.HasSelectedAnnotation); vm.SelectedImage = vm.Images[0]; vm.SelectedBox = vm.Boxes[0]; Assert.True(vm.HasSelectedAnnotation);
        }
        finally { await page.DisposeAsync(); window.Close(); }
    });

    [Fact]
    public void RealStarterAndDatasetHandoffRemainIsolatedAndNeverStartTraining() => Sta(async () =>
    {
        using var files = new Files(); using var session = new EditorSession(); await using var real = new AiWorkspaceClient();
        await using var client = new NoTrainingClient(real); var repository = Repository(); var yolo = Path.Combine(Directory.GetParent(repository)!.FullName, "YOLOStudio");
        var settings = new AiServiceSettings { YoloRoot = yolo, PythonPath = Path.Combine(yolo, ".venv/Scripts/python.exe"), DataHome = Path.Combine(files.Root, "data"), TransferRoot = Path.Combine(files.Root, "transfer"), ServiceScript = Path.Combine(repository, "tools/ai_workspace_service/service.py") };
        await using var vm = new AiWorkspaceViewModel(session, client, settings); await vm.ConnectAsync(); vm.TrainingModel = "keep-my-selection.pt";
        await vm.PrepareStarterExampleAsync(false); var projectId = vm.SelectedProject!.Id; Assert.Equal(12, vm.Images.Count); Assert.Equal(4, vm.Classes.Count);
        await vm.PrepareStarterExampleAsync(false); Assert.Single(vm.Projects); Assert.Equal(projectId, vm.SelectedProject!.Id);
        await vm.StartSlicesAsync(true);
        await Until(async () => { await vm.PollAsync(); return vm.CanUsePreparedDataset || vm.Tasks.Any(t => t.Kind == "dataset" && t.Status == "failed"); });
        Assert.True(vm.CanUsePreparedDataset, vm.Error + vm.DataPreparationStatus);
        var requested = false; vm.TrainingPageRequested += () => requested = true; vm.UsePreparedDatasetForTraining();
        Assert.True(requested); Assert.True(File.Exists(vm.TrainingDataset)); Assert.Equal("keep-my-selection.pt", vm.TrainingModel);
        Assert.Contains("未", vm.Status); Assert.Equal(0, client.TrainingRequests);
        var training = await client.CallAsync("training.list", new { project_id = projectId }); Assert.Empty(training.GetProperty("items").EnumerateArray());
        Assert.Empty(session.Nodes); Assert.False(session.HasChanges);
    });

    private static async Task Initialize(AiWorkspaceViewModel vm) { await vm.ConnectAsync(); vm.SelectedProject = vm.Projects[0]; await Until(() => Task.FromResult(vm.Images.Count == 1)); }
    private static async Task Until(Func<Task<bool>> predicate) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!await predicate()) { if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException(); await Task.Delay(40); } }
    private static void Sta(Func<Task> body)
    {
        Exception? failure = null; var thread = new Thread(() => { var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher)); dispatcher.BeginInvoke(new Action(async () => { try { await body(); } catch (Exception ex) { failure = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run(); });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(80))); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static string Repository() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VisionPlatform.sln"))) directory = directory.Parent; return directory?.FullName ?? throw new DirectoryNotFoundException(); }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "v5fix-" + Guid.NewGuid().ToString("N")[..8]);
        public string Model => Path.Combine(Root, "validation-only.pt"); public string Dataset => Path.Combine(Root, "data.yaml"); public string Onnx => Path.Combine(Root, "validation-only.onnx"); public string Image => Path.Combine(Root, "sample.png");
        public Files() { Directory.CreateDirectory(Root); File.WriteAllText(Model, "client-validation-only"); File.WriteAllText(Dataset, "names: [defect]\n"); File.WriteAllText(Onnx, "client-validation-only"); File.Copy(Path.Combine(Repository(), "AIExamples/starter-v1/source-01.png"), Image); }
        public void Dispose() { var full = Path.GetFullPath(Root); if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("v5fix-")) throw new InvalidOperationException("Fixture path guard"); Directory.Delete(full, true); }
    }
    private sealed class RecordingClient(Files files) : IAiWorkspaceClient
    {
        public List<(string Method, JsonElement Payload)> Requests { get; } = [];
        public bool IsRunning { get; private set; } public AiServiceSettings Settings { get; private set; } = new();
        public event EventHandler<AiServiceEvent>? EventReceived { add { } remove { } }
        public Task StartAsync(AiServiceSettings settings, CancellationToken ct = default) { Settings = settings; IsRunning = true; return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken ct = default)
        {
            Requests.Add((method, JsonSerializer.SerializeToElement(payload ?? new { })));
            object response = method switch
            {
                "hello" => new { workspace_id = "fixture-workspace" }, "projects.list" => new { items = new[] { new { id = "fixture-project", name = "独立测试" } } },
                "context.select" => new { settings = new { } }, "classes.list" => new { items = new[] { new { class_id = 0, name = "测试斑块", color = "#2C7BE5" } } },
                "images.list" => new { total = 1, items = new[] { new { id = "fixture-image", filename = "sample.png", managed_absolute = files.Image, width = 320, height = 240 } } },
                "annotations.get" => new { revision = "initial", boxes = new[] { new { id = "preserve-original-id", class_id = 0, x = 12.1234567890123, y = 15.9876543210987, w = 31.123456789, h = 24.987654321 } } },
                "preview" => new { path = files.Image, width = 320, height = 240 }, "annotations.save" => new { revision = Guid.NewGuid().ToString() },
                "training.start" or "training.resume" => new { task_id = "original-task", status = "recording-only" }, _ => new { items = Array.Empty<object>() }
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(response));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoTrainingClient(IAiWorkspaceClient inner) : IAiWorkspaceClient
    {
        public int TrainingRequests; public bool IsRunning => inner.IsRunning; public AiServiceSettings Settings => inner.Settings;
        public event EventHandler<AiServiceEvent>? EventReceived { add => inner.EventReceived += value; remove => inner.EventReceived -= value; }
        public Task StartAsync(AiServiceSettings s, CancellationToken ct = default) => inner.StartAsync(s, ct);
        public Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken ct = default) { if (method is "training.start" or "training.resume") { TrainingRequests++; throw new InvalidOperationException("Real training forbidden in acceptance tests."); } return inner.CallAsync(method, payload, ct); }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

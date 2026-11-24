using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.AI;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("AiWorkspaceAcceptance <VisionPlatform root> <independent training-report.json> <independent inference-report.json> <output directory>"); return 2; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) => { try { await Verify(args); exit = 0; } catch (Exception e) { Console.Error.WriteLine(e); } finally { app.Shutdown(exit); } };
        app.Run(); return exit;
    }
    private static async Task Verify(string[] args)
    {
        var repository = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[3]); Directory.CreateDirectory(output);
        using var trainingReport = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
        using var inferenceReport = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
        var tr = trainingReport.RootElement; var ir = inferenceReport.RootElement;
        string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
        var dataHome = Text(tr, "data_home"); var projectId = Text(tr.GetProperty("first_record"), "project_id");
        if (!Path.GetFullPath(dataHome).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Acceptance only reuses the explicitly independent temporary training workspace.");
        var settings = new AiServiceSettings { YoloRoot = Text(tr, "code_root"), PythonPath = Text(tr, "python"), DataHome = dataHome, TransferRoot = Path.Combine(Path.GetDirectoryName(dataHome)!, "wpf-render-transfer"), ServiceScript = Path.Combine(repository, "tools", "ai_workspace_service", "service.py") };
        using var session = new EditorSession(); await using var client = new AiWorkspaceClient();
        var page = new AiWorkspacePage(session, client, settings); var vm = page.ViewModel;
        var window = new Window { Title = "VisionStudio AI · 独立验收数据", Width = 1536, Height = 960, Left = -22000, Top = -22000, ShowActivated = false, ShowInTaskbar = false, Content = page };
        var captures = new List<object>();
        try
        {
            window.Show(); await page.InitializeAsync(); Check(!client.IsRunning, "Page initialization must not launch Python.");
            await vm.ConnectAsync(); vm.SelectedProject = vm.Projects.Single(p => p.Id == projectId);
            await Until(() => Task.FromResult(vm.Classes.Count > 0 && vm.Images.Count > 0));
            vm.SelectedImage = vm.Images[0]; await Until(() => Task.FromResult(vm.PreviewPath.Length > 0 && vm.ImageWidth > 0));
            await vm.PollAsync();
            var training = vm.TrainingHistory.FirstOrDefault() ?? throw new InvalidOperationException("The independent workspace did not retain its real training history.");
            vm.SelectedTask = training; await vm.PollAsync(); Check(vm.Metrics.Count > 0, "No real Epoch metrics available.");
            vm.TrainingDataset = Text(tr.GetProperty("config"), "dataset"); vm.TrainingModel = Text(tr.GetProperty("config"), "model"); vm.EpochsText = "2"; vm.BatchText = "1"; vm.InputSizeText = "128";
            vm.TileWidthText = "32"; vm.TileHeightText = "32"; vm.OverlapText = "0.25"; vm.TileIndexText = "-1"; await vm.PreviewSlicesAsync();
            vm.TestModelPath = Text(ir, "model"); vm.TestInputsText = Text(ir, "image"); vm.TestBackend = "onnx"; vm.TestDevice = "cpu"; vm.TestTiled = false;
            await vm.StartInferenceAsync();
            await Until(async () => { await vm.PollAsync(); return vm.Tasks.Any(t => t.Kind == "inference" && !t.Active) && vm.InferenceImages.Count > 0; });
            Check(vm.Detections.Count > 0, "Real original-engine ONNX inference returned no expected detections.");
            Check(vm.InferenceImages[0].ModelHash.Length == 64 && Guid.TryParse(vm.InferenceImages[0].FrameId, out _), "Result identity was not retained.");
            await vm.RefreshModelsAsync(); Check(vm.Models.Count > 0, "Original training models were not retained."); vm.SelectedModel = vm.Models[0];
            await vm.PollAsync();
            var tabs = (TabControl)page.FindName("AiTabs"); var names = new[] { "01-data", "02-annotations", "03-slices", "04-training", "05-inference", "06-models" };
            for (var index = 0; index < 6; index++)
            {
                tabs.SelectedIndex = index; window.UpdateLayout(); await Task.Delay(300); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var path = Path.Combine(output, names[index] + ".png"); Capture(window, path); captures.Add(new { Page = names[index], Path = path, Width = window.ActualWidth, Height = window.ActualHeight, Method = "RenderTargetBitmap of an actually shown, non-activating offscreen WPF window" });
            }
            tabs.SelectedIndex = 1; window.Width = 1366; window.Height = 768; page.LayoutTransform = new ScaleTransform(1.5, 1.5); window.UpdateLayout(); await Task.Delay(250);
            Check(((AiAnnotationCanvas)page.FindName("AnnotationCanvas")).ActualHeight >= 200, "Large text scale compressed annotation viewport to an unusable height.");
            ((ScrollViewer)page.FindName("AiPageScroll")).ScrollToVerticalOffset(230); window.UpdateLayout();
            Capture(window, Path.Combine(output, "07-layout-150-percent.png"));
            var report = new { Passed = true, Timestamp = DateTimeOffset.Now, CurrentSourceRoot = repository,
                Scope = "Shown-window WPF rendering and direct UI ViewModel/service interactions; physical mouse/desktop screenshot is not claimed.",
                Python = settings.PythonPath, IndependentDataHome = settings.DataHome, OriginalUserDataModified = false, ProjectId = projectId,
                Classes = vm.Classes.Select(c => new { c.Id, c.Name, c.AnnotationCount }), ImageCountOnCurrentPage = vm.Images.Count,
                ActualMetrics = vm.Metrics, TrainingHistory = vm.TrainingHistory, Models = vm.Models,
                InferenceImages = vm.InferenceImages.Select(i => new { i.Id, i.ModelHash, i.FrameId, i.TaskId, i.Quality, i.ElapsedMilliseconds, i.SourceKind }),
                DetectionCount = vm.Detections.Count, Captures = captures, LayoutScaleCheck = "Application layout scale 150% at 1366x768; not an OS monitor DPI settings change.",
                InspectionNodes = session.Nodes.Count, InspectionAutomaticallyExecuted = false, StartedNewTraining = false };
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Output = output, Pages = 6, RealEpochRows = vm.Metrics.Count, RealDetections = vm.Detections.Count }));
        }
        finally
        {
            try { await page.PrepareCloseAsync(_ => Task.FromResult(AiCloseChoice.ContinueTraining)); }
            finally { await page.DisposeAsync(); window.Close(); await session.CloseAsync(); }
        }
    }
    private static void Capture(FrameworkElement target, string path)
    {
        target.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth), (int)Math.Ceiling(target.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(target);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<Task<bool>> condition)
    { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("AI WPF acceptance wait timed out."); await Task.Delay(150); } }
}

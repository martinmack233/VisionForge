using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.AI;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("AiInteractionFixAcceptance <repository> <new evidence directory>"); return 2; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) => { try { await Verify(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])); exit = 0; } catch (Exception ex) { Directory.CreateDirectory(args[1]); await File.WriteAllTextAsync(Path.Combine(args[1], "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); } finally { foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) { await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); } app.Shutdown(exit); } };
        app.Run(); return exit;
    }
    private static async Task Verify(string root, string output)
    {
        Directory.CreateDirectory(output); var fixture = Path.Combine(Path.GetTempPath(), "v5fix-ui-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(fixture);
        var yolo = Path.Combine(Directory.GetParent(root)!.FullName, "YOLOStudio"); var state = Path.Combine(output, "workspace");
        var settings = new AiServiceSettings { YoloRoot = yolo, PythonPath = Path.Combine(yolo, ".venv/Scripts/python.exe"), DataHome = Path.Combine(fixture, "data"), TransferRoot = Path.Combine(fixture, "transfer"), ServiceScript = Path.Combine(root, "tools/ai_workspace_service/service.py") };
        await new AiServiceSettingsStore(state).SaveAsync(settings);
        var session = new EditorSession(); var window = new MainWindow(session, state) { Left = -22000, Top = -22000, ShowInTaskbar = false, ShowActivated = true, Width = 1440, Height = 960 };
        window.Show(); await window.WorkspaceReady; var page = window.AiPage; var vm = page.ViewModel; var tabs = (TabControl)page.FindName("AiTabs");
        Check(!vm.IsConnected && session.Results.Count == 0, "Startup executed AI or detection.");
        window.Navigate(WorkspacePage.Examples); await Capture(window, output, "01-examples-entry.png");
        var entry = Walk(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "OpenAiStarter"); entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(window.Workspace.CurrentPage == WorkspacePage.AI && ((Expander)page.FindName("StarterGuide")).IsExpanded && !vm.IsConnected, "AI entry was not offline and explicit.");
        await Capture(window, output, "02-offline-guide.png");
        await vm.ConnectAsync(); await vm.PrepareStarterExampleAsync(false); var projectId = vm.SelectedProject!.Id; Check(vm.Images.Count == 12 && vm.Classes.Count == 4, "Real import did not prepare expected resources.");
        await vm.PrepareStarterExampleAsync(false); Check(vm.Projects.Count == 1 && vm.SelectedProject!.Id == projectId, "Repeated entry duplicated the example.");
        ((Expander)page.FindName("StarterGuide")).IsExpanded = false;
        await Until(async () => { await Task.Yield(); return vm.HasAnnotationImage && vm.Boxes.Count == 4; });
        await Capture(window, output, "03-imported-data.png"); tabs.SelectedIndex = 1; vm.SelectedBox = vm.Boxes[0];
        var originalId = vm.SelectedBox.Id; var imageId = vm.SelectedImage!.Id; vm.AnnotationDraft.XText = "30.1234567890123"; vm.ApplyAnnotationCoordinates(); await vm.FlushAnnotationsAsync();
        var precise = vm.SelectedBox.X; Check(vm.AnnotationDraft.XText == "30.123", "Coordinate display is not compact.");
        vm.ApplyAnnotationCoordinates(); await vm.FlushAnnotationsAsync(); Check(vm.SelectedBox.X == precise && vm.SelectedBox.Id == originalId, "Display refresh rounded data or replaced the ID.");
        await Capture(window, output, "04-annotation-groups.png");
        var sidebar = (ScrollViewer)page.FindName("AnnotationPropertiesScroll"); Check(sidebar.Padding == new Thickness(14), "Sidebar padding is not shared."); sidebar.ScrollToBottom();
        await Capture(window, output, "05-coordinate-actions.png");
        vm.AnnotationDraft.XText = "invalid-coordinate"; vm.ApplyAnnotationCoordinates(); Check(vm.AnnotationEditError.Length > 0 && vm.AnnotationDraft.XText == "invalid-coordinate" && vm.SelectedBox.X == precise, "Invalid edit changed annotation data.");
        vm.AnnotationDraft.XText = "30.1234567890123"; vm.ApplyAnnotationCoordinates(); await vm.FlushAnnotationsAsync();
        window.Width = 1050; window.Height = 660; await Capture(window, output, "06-narrow-annotation.png");
        Check(((Button)page.FindName("ApplyAnnotationButton")).IsEnabled && sidebar.ScrollableHeight > 0, "Narrow layout removed editable actions or scrolling.");
        window.Width = 1440; window.Height = 960; tabs.SelectedIndex = 2; await vm.PreviewSlicesAsync(); Check(!vm.CanUsePreparedDataset, "Preview incorrectly became a training dataset.");
        await Capture(window, output, "07-slice-preview.png");
        await vm.StartSlicesAsync(false); await Until(async () => { await vm.PollAsync(); return vm.Tasks.Any(t => t.Kind == "slices" && t.Status is "completed" or "failed"); });
        Check(!vm.CanUsePreparedDataset && vm.DataPreparationStatus.Contains("没有开始训练"), "Slice completion was reported as training.");
        await vm.StartSlicesAsync(true); await Until(async () => { await vm.PollAsync(); return vm.CanUsePreparedDataset || vm.Tasks.Any(t => t.Kind == "dataset" && t.Status == "failed"); });
        Check(vm.CanUsePreparedDataset, vm.DataPreparationStatus + vm.Error);
        await Capture(window, output, "08-dataset-ready.png"); vm.TrainingModel = "";
        var handoff = Walk(page).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "UseDatasetForTraining"); handoff.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(tabs.SelectedIndex == 3 && vm.TrainingDataset == vm.PreparedDatasetPath && vm.TrainingModel == "", "Handoff started training or replaced the model draft.");
        await Capture(window, output, "09-training-handoff.png");
        var train = Walk(page).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "AiTrain"); train.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var modelInput = (TextBox)page.FindName("TrainingModelInput");
        Check(vm.TrainingModelError == "请先选择初始权重。", "Missing model did not produce the specified inline error."); Check(modelInput.IsKeyboardFocused || ReferenceEquals(FocusManager.GetFocusedElement(window), modelInput), "The invalid model field was not focused.");
        Check(!vm.TrainingHistory.Any() && !vm.Tasks.Any(t => t.Kind == "training"), "A training task was created.");
        await Capture(window, output, "09-missing-weight-focus.png");
        window.Width = 1050; window.Height = 660; modelInput.BringIntoView(); await Capture(window, output, "10-narrow-training.png");
        await page.PrepareCloseAsync(_ => Task.FromResult(AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true);
        Check(!Directory.Exists(settings.TransferRoot) || Directory.GetFiles(settings.TransferRoot, "*.png").Length == 0, "Own preview files were retained after close.");
        await using (var reopened = new AiWorkspaceClient())
        {
            await reopened.StartAsync(settings); var annotations = await reopened.CallAsync("annotations.get", new { project_id = projectId, image_id = imageId });
            var persisted = annotations.GetProperty("boxes").EnumerateArray().Single(b => b.GetProperty("id").GetString() == originalId);
            Check(persisted.GetProperty("x").GetDouble() == precise, "Actual service/SQLite reload lost coordinate precision.");
            var training = await reopened.CallAsync("training.list", new { project_id = projectId }); Check(training.GetProperty("items").GetArrayLength() == 0, "A real training task was unexpectedly created.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Passed = true, Scope = "Current actually shown WPF window, programmatic UI events, real Python import/annotation/slice/dataset services, RenderTargetBitmap captures; no physical mouse screenshot or OS DPI switch claimed.", DataHome = settings.DataHome, WorkspaceState = state, ProjectId = projectId, Images = 12, Classes = 4, AnnotationId = originalId, OriginalPrecisionRetained = precise, ServerPrecisionVerifiedAfterReopen = true, RepeatedEntryReusedProject = true, Dataset = vm.PreparedDatasetPath, DatasetHandoffDidNotChooseWeightsOrStartTraining = true, MissingModelInlineAndFocus = true, TrainingTasksCreated = 0, GpuTrainingStarted = false, ModelsDownloaded = false, WindowSizes = new[] { "1440x960", "1050x660" }, SourceOriginalUserDataModified = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("AI interaction fix shown-window + real service acceptance passed; training tasks = 0.");
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent) { yield return parent; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i))) yield return child; }
    private static async Task Capture(Window target, string output, string name)
    {
        target.UpdateLayout(); await Task.Delay(180); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<Task<bool>> condition) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!await condition()) { if (clock.Elapsed.TotalSeconds > 45) throw new TimeoutException(); await Task.Delay(60); } }
}

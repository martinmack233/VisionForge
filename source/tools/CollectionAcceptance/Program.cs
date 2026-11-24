using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Collections;
using VisionPlatform.Plugins.Samples;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) => { MainWindow? window = null; try
        {
            var root = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            var examples = Path.Combine(output, "isolated-examples"); await CollectionExampleFactory.SaveAllAsync(examples);
            using var session = new EditorSession(); window = new(session, Path.Combine(output, "workspace")) { Width = 1440, Height = 940, Left = -20000, Top = -20000, ShowInTaskbar = false };
            window.Show(); await window.WorkspaceReady;
            await session.OpenAsync(Path.Combine(examples, "collection-multi-ocr/project.json")); window.Navigate(WorkspacePage.Editor); await session.RunAsync();
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display"); window.UpdateLayout(); ((VisionPlatform.Editor.Wpf.Graph.GraphEditorView)window.FindName("GraphView")).FitToContent();
            var view = (CollectionResultsView)window.FindName("CollectionsView"); ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 2;
            Check(view.ResultTable.Items.Count == 3, "Collection result table lacks actual OCR rows.");
            view.ResultTable.SelectedIndex = 1; Check(session.CollectionSelection?.Origin.SourceIndex == 1, "Selection did not bind the corresponding source ROI.");
            await Capture(window, output, "01-ocr-results.png");
            var display = session.CollectionDisplay!; var exact = ((IterationItem)display.Collection.Items[1]).Result!.Confidence;
            session.CommitParameters(session.SelectedNode, JsonDefaults.Element(display.Options with { FontSize = 22, LineWidth = 4, Position = CollectionLabelPosition.BottomLeft, DecimalPlaces = 2, BackgroundOpacity = .6 }));
            Check(session.CollectionDisplay is null && session.CollectionSelection is null, "Style edit retained stale results."); await session.RunAsync(); session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display");
            Check(((IterationItem)session.CollectionDisplay!.Collection.Items[1]).Result!.Confidence == exact, "Formatting changed algorithm precision."); await Capture(window, output, "02-configured-style.png");
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "each"); Check(session.IterationTraces.Count == 3, "Missing bounded debug iterations.");
            Check(session.InspectIterationOutput(1, "ocr", "Text"), "Iteration text snapshot missing."); Check(session.SelectedOutputInspection!.Fields.Count > 0, "Missing iteration details."); await Capture(window, output, "03-iteration-debug.png");
            var dialog = new SubflowEditorWindow(session.Registry, session.ResourceRoot) { Owner = window, Left = -18000, Top = -18000, ShowInTaskbar = false };
            await dialog.LoadBodyAsync(CollectionSubflows.Ocr()); dialog.Show(); await Capture(dialog, output, "04-subflow-editor.png"); dialog.Close();
            window.Width = 1000; window.Height = 650; await Capture(window, output, "05-narrow-results.png");
            var saved = Path.Combine(examples, "collection-multi-ocr/saved.json"); await session.SaveAsync(saved); await session.NewAsync(); Check(session.DisplayImage is null && session.CollectionDisplay is null, "Project change leaked result references."); await session.OpenAsync(saved); await session.RunAsync();
            await session.OpenAsync(Path.Combine(examples, "collection-blob-measure/project.json")); await session.RunAsync(); session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display"); window.Width = 1440; window.Height = 940;
            Check(session.CollectionDisplay!.Collection.Items.Select(i => i.State).SequenceEqual(new[] { BusinessState.Ng, BusinessState.Ok, BusinessState.Ok }), "Blob measurements/judgements differ from fixture."); await Capture(window, output, "06-blob-results.png");
            await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Passed = true, Ocr = new[] { "VISION", "123456", "PART42" }, BlobMeasurements = new[] { 10, 15, 25 }, Selection = true, StyleInvalidation = true, PrecisionPreserved = true, IterationTrace = true, SubflowEditorShown = true, SaveReopen = true, NarrowWindow = "1000x650", Capture = "Actually shown WPF windows with programmatic interactions and RenderTargetBitmap; separate native interaction recorded separately.", TrainingStarted = false, UserDataUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
            await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); window = null; exit = 0;
        }
        catch (Exception ex) { Directory.CreateDirectory(args[1]); await File.WriteAllTextAsync(Path.Combine(args[1], "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
        finally { if (window is not null) { await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); } app.Shutdown(exit); } };
        app.Run(); return exit;
    }
    private static async Task Capture(Window target, string root, string name)
    { target.UpdateLayout(); await Task.Delay(160); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name)); encoder.Save(file); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

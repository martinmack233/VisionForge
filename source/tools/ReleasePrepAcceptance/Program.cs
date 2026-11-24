using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Build;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Collections;
using VisionPlatform.Editor.Wpf.Parameters;
using VisionPlatform.Plugins.Samples;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); var examples = Path.Combine(output, "isolated-examples"); await CollectionExampleFactory.SaveAllAsync(examples);
                using (var serial = new System.IO.Ports.SerialPort()) Check(!serial.IsOpen, "Serial diagnostic unexpectedly opened hardware.");
                using var session = new EditorSession(); window = new(session, Path.Combine(output, "workspace")) { Width = 1440, Height = 940, Left = -20000, ShowInTaskbar = false }; window.Show(); await window.WorkspaceReady;
                await session.OpenAsync(Path.Combine(examples, "collection-multi-ocr/project.json")); window.Navigate(WorkspacePage.Editor); await session.RunAsync();
                session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display"); var parameters = (ParameterPanel)window.FindName("ParametersView");
                Check(Find<DisplayFieldSelector>(parameters).Single().AvailableKeys.Contains("Confidence"), "Actual OCR confidence field missing.");
                Check(Find<ColorParameterEditor>(parameters).Any() && Find<DisplayNumberEditor>(parameters).Any(), "Display controls absent.");
                await Capture(window, output, "01-display-fields.png");
                var opacity = parameters.Draft!.Fields.Single(f => f.Descriptor.Path == "BackgroundOpacity"); opacity.Text = ".851234567890123";
                Check(session.CurrentOutputs.Count > 0, "Draft edit executed or invalidated before Apply."); Check(parameters.ApplyChanges(), "Could not apply display draft."); Check(session.CurrentOutputs.Count == 0 && session.CollectionDisplay is null, "Old results remained valid after Apply.");
                await session.RunAsync(); var path = Path.Combine(examples, "collection-multi-ocr/saved.json"); await session.SaveAsync(path); await session.OpenAsync(path);
                Check(session.Nodes.Single(n => n.NodeId == "display").Parameters.GetProperty("BackgroundOpacity").GetDouble() == .851234567890123, "Display precision lost across save."); await session.RunAsync();
                var picker = new ColorPickerWindow("#2C7BE5") { Owner = window, Left = -18000, ShowInTaskbar = false }; picker.Show(); await Capture(picker, output, "02-color-picker.png"); picker.Close();
                var child = new SubflowEditorWindow(session.Registry, session.ResourceRoot) { Owner = window, Left = -18000, ShowInTaskbar = false };
                var body = CollectionSubflows.Ocr(); await child.LoadBodyAsync(body with { Connections = body.Connections.Where(c => c.TargetPort != "Image" && c.TargetPort != "OCR").ToList() }); child.Show();
                Find<Button>(child).Single(b => Equals(b.Content, "保存子流程")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(Find<ListBox>(child).Any(list => list.Items.Cast<object>().Any(i => i is VisionPlatform.Editor.Core.Parameters.SubflowProblem { NodeId: "ocr", Port: "Image" })), "Problem node/port not located.");
                await Capture(child, output, "03-subflow-problems.png"); child.Close();
                window.Navigate(WorkspacePage.Settings); await Capture(window, output, "04-about-version.png"); Check(window.Workspace.Version.Contains(ProductBuild.Version) && window.Workspace.Version.Contains(ProductBuild.SourceHash), "About identity mismatch.");
                window.Navigate(WorkspacePage.Editor); session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display"); window.Width = 1000; window.Height = 650; await Capture(window, output, "05-narrow-parameters.png");
                await session.OpenAsync(Path.Combine(examples, "collection-blob-measure/project.json")); await session.RunAsync(); session.SelectedNode = session.Nodes.Single(n => n.NodeId == "display");
                Check(Find<DisplayFieldSelector>(parameters).Single().AvailableKeys.Contains("Number"), "Blob actual measurement fields absent.");
                await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Passed = true, ProductVersion = ProductBuild.Version, ProductBuild.SourceHash, ProductBuild.SourceState, ProductBuild.Runtime,
                    FieldsFromActualInput = true, ColorPickerShown = true, NumericPrecision = true, AppliedResultsInvalidated = true, SaveReopen = true, SubflowProblemsLocated = true, About = true, SerialManagedLoadAndConstruction = true, SerialHardwareOpened = false, NarrowWindow = "1000x650", Capture = "Actually shown WPF windows with programmatic component interactions and RenderTargetBitmap; native desktop evidence is separate.", UserDataUsed = false, TrainingStarted = false, CameraOpened = false }, new JsonSerializerOptions { WriteIndented = true }));
                await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); window = null; exit = 0;
            }
            catch (Exception ex) { Directory.CreateDirectory(args[0]); await File.WriteAllTextAsync(Path.Combine(args[0], "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { if (window is not null) { await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); } app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static async Task Capture(Window target, string root, string name) { target.UpdateLayout(); await Task.Delay(120); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); var bitmap = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name)); png.Save(file); }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject { if (parent is T value) yield return value; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

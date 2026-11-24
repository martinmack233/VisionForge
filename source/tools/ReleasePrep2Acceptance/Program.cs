using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Build;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Deployment;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Deployment;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var code = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) =>
        {
            MainWindow? main = null;
            try
            {
                var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                var machinePath = Path.Combine(root, "machine-v1.json"); Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG", machinePath);
                var config = new MachineConfiguration { DataDirectory = Path.Combine(root, "user-data"), CacheDirectory = Path.Combine(root, "cache"), LogDirectory = Path.Combine(root, "logs") };
                using var session = new EditorSession(); main = new(session, Path.Combine(root, "editor-state")) { Width = 1280, Height = 880, Left = -20000, ShowInTaskbar = false }; main.Show(); await main.WorkspaceReady;
                var setup = new SetupWindow(new(machinePath)) { Owner = main, Left = -18000, ShowInTaskbar = false }; setup.SetConfiguration(config); setup.Show(); await setup.CheckAsync();
                Check(setup.Report!.Checks.Single(c => c.Id == "core").State == SetupState.Available, "Core unavailable without AI."); Check(setup.Report.Checks.Single(c => c.Id == "ai-runtime").State == SetupState.Unconfigured, "Optional runtime was implicitly configured.");
                await Capture(setup, root, "01-setup-core-only.png"); await setup.SaveConfigurationAsync(); setup.Close();
                var examples = Path.Combine(root, "isolated-examples"); await CollectionExampleFactory.SaveAllAsync(examples);
                var versions = new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" }; var packages = new ProjectPackageStore(session.Registry, versions); var outcomes = new List<object>();
                foreach (var name in new[] { "collection-multi-ocr", "collection-blob-measure" })
                {
                    var source = Path.Combine(examples, name, "project.json"); var package = Path.Combine(root, name + ".vspkg"); await packages.ExportAsync(source, package);
                    var imported = await packages.ImportAsync(package, new(Path.Combine(root, "imported", name), name + " imported"));
                    await main.Workspace.OpenProjectAsync(imported); await session.RunAsync(); Check(session.Results.All(r => r.Status == "Success"), "Imported graph failed in WPF.");
                    outcomes.Add(new { Name = name, ImportedProject = imported, Status = "Success", OutputNodes = session.CurrentOutputs.Count });
                }
                var packageWindow = new ProjectPackageWindow(session, () => Task.FromResult(true)) { Owner = main, Left = -18000, ShowInTaskbar = false };
                packageWindow.Show(); var chosen = Path.Combine(root, "collection-multi-ocr.vspkg"); ((TextBox)packageWindow.FindName("PackagePath")).Text = chosen; await packageWindow.InspectPackageAsync(chosen);
                ((TabControl)packageWindow.FindName("PackageTabs")).SelectedIndex = 1;
                var target = Path.Combine(root, "wpf-package-window-import"); ((TextBox)packageWindow.FindName("Destination")).Text = target;
                await packageWindow.ImportPackageAsync(chosen, target, "WPF导入与重开"); Check(File.Exists(packageWindow.ImportedProject), "UI import did not create project.");
                await Capture(packageWindow, root, "02-package-preflight-import.png"); packageWindow.Width = 780; packageWindow.Height = 570; await Capture(packageWindow, root, "03-package-narrow.png"); packageWindow.Close();
                await session.OpenAsync(Path.Combine(target, "project.json")); await session.RunAsync(); Check(session.Results.All(r => r.Status == "Success"), "UI imported project did not reopen/run.");
                var aiChecked = false;
                if (args.Length >= 4)
                {
                    config = config with { DetectionComponentRoot = Path.GetFullPath(args[1]), DetectionPython = Path.GetFullPath(args[2]), SetupCompleted = true }; await new MachineConfigurationStore(machinePath).SaveAsync(config);
                    var model = Path.GetFullPath(args[3]); var verified = await YoloDeploymentVerifier.VerifyAsync(model);
                    var onnxSetup = new SetupWindow(new(machinePath)) { Owner = main, Left = -18000, ShowInTaskbar = false }; onnxSetup.Show(); ((TextBox)onnxSetup.FindName("ModelPath")).Text = model;
                    await onnxSetup.CheckAsync(); Check(onnxSetup.Report!.Checks.Single(c => c.Id == "ai-runtime").State == SetupState.Available, "Independent detection component unavailable."); Check(onnxSetup.Report.Checks.Single(c => c.Id == "model").State == SetupState.Available, "Actual ONNX input validation failed.");
                    await SetupDiagnostics.ExportRedactedAsync(Path.Combine(root, "diagnostic-redacted.json"), onnxSetup.Report); await Capture(onnxSetup, root, "04-independent-ai-runtime.png"); onnxSetup.Close();
                    var inference = new YoloInferenceOptions { ModelPath = model, ImageSize = verified.InputSize, ExpectedModelSha256 = verified.ModelSha256, ExpectedDeploymentSha256 = verified.DeploymentSha256, DeploymentContractJson = verified.Deployment.GetRawText(), ExpectedClassNamesJson = verified.Deployment.GetProperty("names").GetRawText() };
                    var aiProject = new ProjectDocument { ProjectName = "本机模型外部引用验收", Nodes = [DemoProjectFactory.Node("input", "ImageInput", new ImageInputParameters { Path = Path.Combine(examples, "collection-blob-measure/input.png") }), DemoProjectFactory.Node("yolo", "YoloInference", inference)], Connections = [new("input", "Image", "yolo", "Image")] };
                    var aiDirectory = Path.Combine(root, "ai-project"); var projectPath = Path.Combine(aiDirectory, "project.json"); await new ProjectStore(session.Registry, versions).SaveAsync(projectPath, aiProject);
                    await session.OpenAsync(projectPath); await session.RunAsync(); Check(session.Results.All(r => r.Status == "Success"), "AI runtime required training environment/source.");
                    Check(!main.AiPage.ViewModel.IsConnected, "Training service was implicitly connected.");
                    await packages.ExportAsync(projectPath, Path.Combine(root, "external-model.vspkg"));
                    var modelManifest = (await packages.InspectAsync(Path.Combine(root, "external-model.vspkg"))).Manifest;
                    var modelId = modelManifest.Resources.Single(r => r.Kind == "Model").Id;
                    var relocated = await packages.ImportAsync(Path.Combine(root, "external-model.vspkg"), new(Path.Combine(root, "ai-imported"), Relocations: new Dictionary<string, string> { [modelId] = model }));
                    await session.OpenAsync(relocated); await session.RunAsync(); Check(session.Results.All(r => r.Status == "Success"), "Relocated external model failed.");
                    aiChecked = true;
                }
                await File.WriteAllTextAsync(Path.Combine(root, "summary.json"), JsonSerializer.Serialize(new { Passed = true, ProductBuild.Version, ProductBuild.SourceHash, ProductBuild.SourceState, ProductBuild.Runtime, Packages = outcomes, OptionalTrainingNotConnected = !main.AiPage.ViewModel.IsConnected, IndependentAiChecked = aiChecked, Framework = ".NET10", ActualShownWindows = true, NativeInput = false, UserDataUsed = false, ModelSource = aiChecked ? "existing local ONNX read-only; not redistributed" : "none", CleanWindows = false, TrainingStarted = false, RealCameraOpened = false }, new JsonSerializerOptions { WriteIndented = true }));
                await main.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await main.CloseEditorAsync(true); main = null; code = 0;
            }
            catch (Exception ex) { Directory.CreateDirectory(args[0]); await File.WriteAllTextAsync(Path.Combine(args[0], "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { if (main is not null) { await main.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await main.CloseEditorAsync(true); } app.Shutdown(code); }
        };
        app.Run(); return code;
    }
    private static DependencyObject FindParentOfName(Window window, string name, Type type)
    {
        DependencyObject? current = (DependencyObject)window.FindName(name);
        while (current is not null && !type.IsInstanceOfType(current)) current = VisualTreeHelper.GetParent(current);
        return current ?? throw new InvalidOperationException("Tab parent missing.");
    }
    private static async Task Capture(Window window, string root, string name)
    {
        window.UpdateLayout(); await Task.Delay(120); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(root, name)); encoder.Save(stream);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

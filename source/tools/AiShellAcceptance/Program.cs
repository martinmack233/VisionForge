using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Imaging;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 5) { Console.Error.WriteLine("AiShellAcceptance <root> <training report> <export report> <published directory> <new output>"); return 2; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) => { try { await Verify(args); exit = 0; } catch (Exception ex) { Console.Error.WriteLine(ex); } finally { foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) await window.CloseEditorAsync(true); app.Shutdown(exit); } };
        app.Run(); return exit;
    }
    private static async Task Verify(string[] args)
    {
        var root = Path.GetFullPath(args[0]); var published = Path.GetFullPath(args[3]); var output = Path.GetFullPath(args[4]); Directory.CreateDirectory(output);
        using var training = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])); using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(args[2])); var tr = training.RootElement; var ex = exported.RootElement;
        string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;
        var data = Text(tr, "data_home"); if (!Path.GetFullPath(data).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only independent Temp training data is allowed.");
        var settings = new AiServiceSettings { YoloRoot = Text(tr, "code_root"), PythonPath = Text(tr, "python"), DataHome = data, TransferRoot = Path.Combine(output, "transfer"), ServiceScript = Path.Combine(root, "tools/ai_workspace_service/service.py") };
        var state = Path.Combine(output, "workspace"); await new AiServiceSettingsStore(state).SaveAsync(settings);
        var before = FrameHandle.ActiveBytes; using var session = new EditorSession();
        var window = new MainWindow(session, state) { Left = -22000, Top = -22000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); await window.WorkspaceReady; var vm = window.AiPage.ViewModel;
        Check(!vm.IsConnected && session.Results.Count == 0, "Initialization unexpectedly ran Python or detection.");
        var imagePath = Path.Combine(output, "input.png"); using (var builder = new ImageFrameBuilder(64, 64, PixelFormat.Mono8)) { using var image = builder.Build(); ImageFile.Save(image, imagePath); }
        await session.ImportImageAsync(imagePath); var imageId = session.Nodes.Single(n => n.TypeId == "ImageInput").NodeId;
        await vm.ConnectAsync(); vm.SelectedProject = vm.Projects.Single(p => p.Id == Text(tr.GetProperty("first_record"), "project_id"));
        await Until(() => vm.HasProject && vm.Models.Any(m => m.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)));
        vm.InputSizeText = "64"; vm.SelectedModel = vm.Models.Single(m => m.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
        await vm.ApplyModelToNodeAsync(true);
        var yolo = session.Nodes.Single(n => n.TypeId == "YoloInference");
        Check(session.CurrentOutputs.Count == 0 && session.Results.Count == 0, "Applying the model executed detection.");
        Check(yolo.Parameters.GetProperty("ExpectedModelSha256").GetString() == Text(ex, "export_model_sha256"), "The actual model identity was not pinned by the Shell callback.");
        Check(session.SnapshotProject().AiProject is not null, "Shell did not associate independent AI identities.");
        Check(session.TryConnect(session.Nodes.Single(n => n.NodeId == imageId).Outputs.Single(p => p.Name == "Image"), yolo.Inputs.Single(p => p.Name == "Image"), out _), "Cannot connect the AI output to the normal Runtime graph.");
        var project = Path.Combine(output, "shell-applied-project.json"); await session.SaveAsync(project); await session.OpenAsync(project); await session.RunAsync();
        Check(session.Results.All(r => r.Status == "Success"), "The Shell-applied graph did not execute through Runtime after reopen.");
        foreach (var (exe, arguments) in new[] { ("VisionPlatform.Console.exe", new[] { "run", project }), ("VisionPlatform.RuntimeHost.exe", new[] { "--project", project, "--once" }) })
        {
            var start = new ProcessStartInfo(Path.Combine(published, exe)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = published }; foreach (var arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await File.WriteAllTextAsync(Path.Combine(output, exe + ".log"), await stdout + "\n" + await stderr); Check(process.ExitCode == 0, exe + " rejected the WPF-applied model.");
        }
        var result = session.CurrentOutputs[yolo.NodeId]["Result"]; await File.WriteAllTextAsync(Path.Combine(output, "actual-result.json"), JsonSerializer.Serialize(result, result.GetType(), JsonDefaults.Options));
        await window.CloseEditorAsync(true); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Check(FrameHandle.ActiveBytes == before, "Shell close retained image buffers.");
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new { Passed = true, AppliedViaActualMainWindowCallback = true, OnlyEditedUntilExplicitRun = true, SavedReopened = true, RuntimeConsoleHostPassed = true, ModelHash = Text(ex, "export_model_sha256"), OriginalUserDataModified = false, BuffersBefore = before, BuffersAfter = FrameHandle.ActiveBytes }, JsonDefaults.Options));
        Console.WriteLine("AI Shell apply/save/reopen/Runtime/Console/Host passed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition) { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException(); await Task.Delay(50); } }
}

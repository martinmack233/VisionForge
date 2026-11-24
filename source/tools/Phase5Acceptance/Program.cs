using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

internal static class Program
{
    private static string _output = "";
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Phase5Acceptance <repository> <published directory> <new evidence directory>"); return 2; }
        _output = Path.GetFullPath(args[2]); Directory.CreateDirectory(_output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; var exit = 1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        app.Startup += async (_, _) => { try { await Verify(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])); exit = 0; } catch (Exception ex) { await Save("failure.json", new { Passed = false, Error = ex.ToString() }); Console.Error.WriteLine(ex); } finally { foreach (var window in app.Windows.OfType<MainWindow>().ToArray()) await window.CloseEditorAsync(true); app.Shutdown(exit); } };
        app.Run(); return exit;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(_output, name), JsonSerializer.Serialize(value, JsonDefaults.Options));
    private static async Task Verify(string root, string published)
    {
        var baseline = FrameHandle.ActiveBytes;
        using var session = new EditorSession();
        var window = new MainWindow(session, Path.Combine(_output, "workspace")) { Left = -22000, Top = -22000, ShowActivated = false, ShowInTaskbar = false, Width = 1366, Height = 768 };
        window.Show(); await window.WorkspaceReady;
        Check(session.Results.Count == 0 && !window.AiPage.ViewModel.IsConnected, "Startup performed detection or started AI.");
        var old = new[] { "tests/Fixtures/legacy-editor-project.json", "samples/phase3a/flow1.json", "samples/phase3a/flow2.json", "samples/phase3a/flow3.json", "samples/phase3b/flow1.json", "samples/phase3c/flow6-enhance-transform.json", "Examples/virtual-camera/project.json" };
        var compatibility = new List<object>();
        foreach (var relative in old)
        {
            var path = Path.Combine(root, relative); var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            await window.OpenProjectAsync(path, true); await session.RunAsync();
            Check(session.Results.Count > 0 && session.Results.All(r => r.Status is "Success" or "Skipped"), "Old engineering flow failed: " + relative);
            compatibility.Add(new { Path = relative, Nodes = session.Nodes.Count, Success = true, OriginalHash = before });
            Check(before == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), "Original project was modified.");
        }
        await session.NewAsync(); session.AddNode("MedianBlur", 15, 20); var blur = session.SelectedNode!; session.AddNode("Threshold", 390, 20); var threshold = session.SelectedNode!;
        Check(session.TryConnect(blur.Outputs.Single(p => p.Name == "ProcessedImage"), threshold.Inputs.Single(p => p.Name == "Image"), out _), "Data connect failed.");
        Check(session.TryConnect(blur.AllOutputs.Single(p => p.Name == NodePorts.Completed), threshold.AllInputs.Single(p => p.Name == NodePorts.Execute), out _), "Control connect failed.");
        var blurId = blur.NodeId; var originalParameters = blur.Parameters.GetRawText();
        var pasted = session.PasteNodes(session.CopyNodes([blur, threshold]));
        Check(pasted.Count == 2 && session.Connections.Count == 4 && pasted.All(p => p.NodeId != blurId), "Copy did not remap internal data/control edges.");
        session.Undo(); Check(session.Nodes.Count == 2 && session.Connections.Count == 2, "Paste undo failed.");
        session.SetNodeEnabled(session.Nodes.Single(n => n.NodeId == blurId), false);
        Check(session.Connections.Count == 0 && session.CurrentOutputs.Count == 0, "Disable retained wires or old outputs.");
        session.Undo(); Check(session.Connections.Count == 2 && session.Nodes.All(n => n.IsEnabled), "Disable undo did not restore both wire kinds.");
        session.Redo(); blur = session.Nodes.Single(n => n.NodeId == blurId); session.SetNodeEnabled(blur, true);
        Check(session.Connections.Count == 0 && blur.Parameters.GetRawText() == originalParameters, "Enable restored wires or lost parameters.");
        session.SetNodeEnabled(blur, false); session.SetNodeEnabled(session.Nodes.Single(n => n.TypeId == "Threshold"), false);
        var independentImage = Path.Combine(_output, "independent.bmp");
        using (var builder = new ImageFrameBuilder(16, 16, PixelFormat.Mono8)) { using var image = builder.Build(); ImageFile.Save(image, independentImage); }
        session.AddNode("ImageInput", 400, 150); session.CommitParameters(session.SelectedNode!, JsonDefaults.Element(new ImageInputParameters { Path = independentImage })); await session.RunAsync();
        Check(session.Nodes.Where(n => !n.IsEnabled).All(n => n.Status == "已禁用"), "Disabled appearance changed after full run.");
        Check(session.Results.Any(r => r.Status == "Success"), "Disabled missing inputs blocked an independent flow.");
        var disabledPath = Path.Combine(_output, "disabled-independent.json"); await session.SaveAsync(disabledPath); await session.OpenConfigurationAsync(disabledPath); await session.RunAsync();
        Check(session.SnapshotProject().SchemaVersion == 3 && session.Nodes.Count(n => !n.IsEnabled) == 2, "Formal disable state was not restored.");
        await Capture(window, "disabled-independent.png");
        var console = await Run(published, "VisionPlatform.Console.exe", "console-disabled.log", "run", disabledPath);
        var host = await Run(published, "VisionPlatform.RuntimeHost.exe", "host-disabled.log", "--project", disabledPath, "--once");
        Check(console.Exit == 0 && console.Text.Contains("Disabled") && host.Exit == 0 && host.Text.Contains("Disabled"), "WPF/Console/Host disagree on disabled execution.");
        session.SetNodeEnabled(session.Nodes.Single(n => n.TypeId == "Threshold"), true); var missingPath = Path.Combine(_output, "missing-input.json"); await session.SaveAsync(missingPath);
        var missingConsole = await Run(published, "VisionPlatform.Console.exe", "console-missing.log", "run", missingPath);
        var missingHost = await Run(published, "VisionPlatform.RuntimeHost.exe", "host-missing.log", "--project", missingPath, "--once");
        Check(missingConsole.Exit != 0 && missingConsole.Text.Contains("Image") && missingHost.Exit != 0 && missingHost.Text.Contains("Image"), "Missing downstream inputs were not reported.");
        await session.NewAsync(); var images = Path.Combine(_output, "camera-inputs"); Directory.CreateDirectory(images);
        using (var builder = new ImageFrameBuilder(64, 48, PixelFormat.Mono8)) { builder.GetRowSpan(4)[7] = 255; using var image = builder.Build(); ImageFile.Save(image, Path.Combine(images, "frame.bmp")); }
        session.AddNode("VirtualCamera"); var camera = session.SelectedNode!;
        session.CommitParameters(camera, JsonDefaults.Element(new VirtualCameraParameters { FolderPath = images, FrameRate = 30 }));
        session.AddNode("ImageDisplay"); Check(session.TryConnect(camera.Outputs.Single(p => p.Name == "Image"), session.SelectedNode!.Inputs.Single(p => p.Name == "Image"), out _), "Camera display connection failed.");
        window.Navigate(WorkspacePage.Devices); var device = window.Devices; device.FolderPath = images; await device.OpenAsync(); await device.CaptureAsync(); var frozen = device.FrameInfo!;
        await device.FreezePreviewAsync(); Check(device.IsFrozen && device.PreviewImage is not null, "Stop did not retain an explicit frozen frame.");
        await device.DetectFrozenAsync(); Check(device.LastDetectionFrameId == frozen.FrameId, "Frozen detection used another frame.");
        await device.RunProjectAsync(); var fresh = (CameraFrameInfo)session.CurrentOutputs[camera.NodeId]["FrameInfo"];
        Check(fresh.FrameId != frozen.FrameId && fresh.Sequence > frozen.Sequence, "Fresh detection reused the preview.");
        await device.StartAsync(); await Until(() => device.FramesReceived >= 4); await device.FreezePreviewAsync(); Check(device.IsFrozen && !device.IsAcquiring, "Continuous stop did not freeze.");
        await Capture(window, "device-frozen.png"); var stats = device.Statistics;
        device.ClearPreviewCommand.Execute(null); Check(device.PreviewImage is null, "Clear retained preview."); await device.CloseAsync();
        foreach (var size in new[] { (1366d, 768d), (1093d, 614d), (911d, 512d) }) { window.Width = size.Item1; window.Height = size.Item2; await Capture(window, $"device-layout-{size.Item1}.png"); }
        await window.CloseEditorAsync(true); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(FrameHandle.ActiveBytes == baseline, "Closing leaked owned image buffers.");
        await Save("summary.json", new { Passed = true, Scope = "Programmatic interactions on actually shown offscreen WPF windows; no physical mouse or OS DPI switch claimed.", Compatibility = compatibility, SchemaVersion = 3, DisabledDataAndControlDisconnected = true, EnableDoesNotReconnect = true, UndoRestoresConnections = true, WpfConsoleHostAgree = true, CopyInternalEdges = true, FrozenFrameId = frozen.FrameId, FreshFrameId = fresh.FrameId, FrozenSequence = frozen.Sequence, FreshSequence = fresh.Sequence, Statistics = stats, StartupDidNotLaunchAiOrDetection = true, BuffersBefore = baseline, BuffersAfter = FrameHandle.ActiveBytes, PhysicalCameraTested = false });
        Console.WriteLine("Phase5 classic acceptance passed.");
    }
    private static async Task<(int Exit, string Text)> Run(string directory, string exe, string log, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, exe)) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var text = await stdout + "\n" + await stderr; await File.WriteAllTextAsync(Path.Combine(_output, log), text); return (process.ExitCode, text);
    }
    private static async Task Capture(Window window, string name)
    {
        window.UpdateLayout(); await Task.Delay(150); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(_output, name)); encoder.Save(stream);
    }
    private static async Task Until(Func<bool> predicate) { var clock = Stopwatch.StartNew(); while (!predicate()) { if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException(); await Task.Delay(50); } }
}

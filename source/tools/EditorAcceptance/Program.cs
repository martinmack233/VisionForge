using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Imaging;
using VisionPlatform.Runtime.Execution;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

namespace EditorAcceptance;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/Dark.xaml") });
        var exit = 1;
        app.Startup += async (_, _) =>
        {
            try { await VerifyAsync(root); exit = 0; }
            catch (Exception ex) { System.Console.Error.WriteLine(ex); }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }

    private static async Task VerifyAsync(string root)
    {
        var directory = Path.Combine(root, "artifacts", "phase2-acceptance");
        Directory.CreateDirectory(directory);
        var beforeBytes = FrameHandle.ActiveBytes;
        var beforeAllocations = FrameHandle.ActiveAllocations;
        var session = new EditorSession();
        var window = new MainWindow(session);
        window.Show();
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        var watch = Stopwatch.StartNew();
        await session.CreateDemoAsync(Path.Combine(directory, "project"));
        window.FitEditorViews();
        await viewport.WaitForPreviewAsync();
        ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1;
        await session.RunAsync();
        var first = Measurements(session);
        Check(first.Match.Success && first.BlobCount == 2 && first.IsOk, "Initial graph did not produce measured OK.");

        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ROITransform");
        session.UpdateRoi(new RotatedRectRoi(165, 100, 55, 38, 5));
        Check(session.Results.Count == 0, "Editing ROI did not invalidate old results.");
        await session.RunAsync();
        var edited = Measurements(session);
        Check(edited.Match.Success && edited.BlobCount == 2 && edited.IsOk, "Edited ROI flow failed.");
        var projectFile = Path.Combine(directory, "saved", "inspection.json");
        await session.SaveAsync(projectFile);
        var document = session.SnapshotProject();
        Check(document.SchemaVersion == 1 && document.ROI.Any(r => Math.Abs(r.AngleDegrees - 5) < 1e-9), "ROI/schema did not persist in official model.");
        using (var headless = await Task.Run(() => new RuntimeExecutor(session.Registry).ExecuteAsync(document.ToGraph(), session.ResourceRoot)))
        {
            var match = headless.Nodes["match"].Output<TemplateMatchResult>("MatchResult");
            Check(match.Success && Math.Abs(match.Score - edited.Match.Score) < 1e-6, "Editor/headless runtime score differs.");
            Check(headless.Nodes["blob"].Output<int>("BlobCount") == edited.BlobCount, "Editor/headless Blob count differs.");
        }
        var consoleFile = Path.Combine(root, "src", "VisionPlatform.Console", "bin", "Release", "net10.0", "VisionPlatform.Console.exe");
        var consoleOutput = Path.Combine(directory, "console");
        var processInfo = new ProcessStartInfo(consoleFile) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", projectFile, "--outputs", consoleOutput }) processInfo.ArgumentList.Add(arg);
        using (var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Console did not start."))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            File.WriteAllText(Path.Combine(directory, "console.log"), await output + await error);
            Check(process.ExitCode == 0, "Saved WPF project did not run successfully in Console.");
        }
        using (var consoleJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(consoleOutput, "results.json"))))
        {
            var output = consoleJson.RootElement.GetProperty("Nodes").GetProperty("match").GetProperty("Outputs").GetProperty("MatchResult");
            Check(Math.Abs(output.GetProperty("Score").GetDouble() - edited.Match.Score) < 1e-6, "Console process score differs from WPF session.");
            Check(Math.Abs(output.GetProperty("CenterX").GetDouble() - edited.Match.CenterX) < 1e-6, "Console process center differs from WPF session.");
        }

        await viewport.WaitForPreviewAsync();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        SaveWindow(window, Path.Combine(directory, "editor-running.png"));
        await window.CloseEditorAsync(true);
        Check(FrameHandle.ActiveBytes == beforeBytes, "Closing first window retained platform image allocations.");

        var reopenedSession = new EditorSession();
        var reopenedWindow = new MainWindow(reopenedSession); reopenedWindow.Show();
        await reopenedSession.OpenAsync(projectFile); reopenedWindow.FitEditorViews();
        await reopenedSession.RunAsync();
        var restored = Measurements(reopenedSession);
        Check(Math.Abs(restored.Match.Score - edited.Match.Score) < 1e-6 && restored.BlobCount == edited.BlobCount && restored.IsOk, "Reopened project measurements differ.");
        var reopenedViewport = (ImageViewportControl)reopenedWindow.FindName("Viewport");

        // Real 82 MP file load and WPF rendering; no large image stored in the source package.
        var largePath = Path.Combine(directory, "large-mono.bmp");
        await Task.Run(() =>
        {
            using var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8);
            for (var y = 4500; y < 4600; y++) builder.GetRowSpan(y).Slice(4000, 120).Fill(220);
            using var large = builder.Build(); ImageFile.Save(large, largePath);
        });
        var ticks = 0; var maximumHeartbeatGap = 0.0; var lastHeartbeat = Stopwatch.GetTimestamp();
        var memoryBefore = ProcessMemory();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(5), DispatcherPriority.Background, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp(); maximumHeartbeatGap = Math.Max(maximumHeartbeatGap, Stopwatch.GetElapsedTime(lastHeartbeat, now).TotalMilliseconds);
            lastHeartbeat = now; ticks++;
        }, Dispatcher.CurrentDispatcher);
        var largeWatch = Stopwatch.StartNew();
        await reopenedSession.ImportImageAsync(largePath);
        await reopenedViewport.WaitForPreviewAsync();
        reopenedViewport.ShowOneToOne();
        var point = new Point2D(4050.25, 4550.75);
        var screen = reopenedViewport.ImageToScreen(point);
        reopenedViewport.ViewTransform.ZoomAt(screen, 1.5);
        reopenedViewport.ViewTransform.Pan(20, -15);
        var restoredPoint = reopenedViewport.ScreenToImage(reopenedViewport.ImageToScreen(point));
        Check(Math.Abs(point.X - restoredPoint.X) < 1e-6 && Math.Abs(point.Y - restoredPoint.Y) < 1e-6, "Large image display changes detection coordinates.");
        Check(reopenedViewport.PreviewBytes <= reopenedViewport.MaxPreviewBytes && reopenedViewport.PreviewWidth <= 2048, "Preview cache is unbounded.");
        Check(ticks > 0, "Dispatcher did not respond during 82MP image loading/preview.");
        timer.Stop();
        var previewBytes = reopenedViewport.PreviewBytes;
        var previewDimensions = new[] { reopenedViewport.PreviewWidth, reopenedViewport.PreviewHeight };
        var largeElapsedMilliseconds = largeWatch.Elapsed.TotalMilliseconds;
        var memoryWithLargePreview = ProcessMemory();
        await reopenedWindow.CloseEditorAsync(true);
        Check(FrameHandle.ActiveBytes == beforeBytes && FrameHandle.ActiveAllocations == beforeAllocations, "Closing reopened/large image window retained platform buffers.");
        // Exercise WPF's real Closing event as well as the explicit close API. A synchronous drain
        // must not cause a reentrant Close inside the initial Closing callback.
        var closingSession = new EditorSession();
        var closingWindow = new MainWindow(closingSession);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closingWindow.Closed += (_, _) => closed.TrySetResult();
        closingWindow.Show(); await closingSession.NewAsync(); closingWindow.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(FrameHandle.ActiveBytes == beforeBytes, "Normal window Closing retained image buffers.");
        var report = new { Passed = true, SchemaVersion = 1, ConsoleMatchesWpf = true, ReopenedMeasurementsMatch = true,
            Edited = edited, LargeImage = new { Width = 8192, Height = 10000, PreviewDimensions = previewDimensions, PreviewBytes = previewBytes,
                DispatcherTicks = ticks, MaximumHeartbeatGapMilliseconds = maximumHeartbeatGap, ElapsedMilliseconds = largeElapsedMilliseconds,
                ProcessBefore = memoryBefore, ProcessWithPreview = memoryWithLargePreview, ProcessAfterClose = ProcessMemory() },
            NormalClosingPassed = true, BuffersBefore = beforeBytes, BuffersAfter = FrameHandle.ActiveBytes, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(report, JsonDefaults.Options));
        System.Console.WriteLine(JsonSerializer.Serialize(report, JsonDefaults.Options));
    }
    private sealed record Measurement(TemplateMatchResult Match, int BlobCount, bool IsOk);
    private static Measurement Measurements(IEditorSession session)
    {
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "TemplateMatching");
        var match = (TemplateMatchResult)session.SelectedOutputs.Single(r => r.PortName == "MatchResult").Value;
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "BlobDetection");
        var count = (int)session.SelectedOutputs.Single(r => r.PortName == "BlobCount").Value;
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "ResultDecision");
        return new(match, count, (bool)session.SelectedOutputs.Single(r => r.PortName == "IsOk").Value);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object ProcessMemory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64 };
    }
    private static void SaveWindow(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;
using Rect = OpenCvSharp.Rect;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        var code = 1;
        app.Startup += async (_, _) =>
        {
            try { await Verify(root); code = 0; }
            catch (Exception ex) { System.Console.Error.WriteLine(ex); }
            finally { app.Shutdown(code); }
        };
        app.Run(); return code;
    }

    private static NodeSpecification Node(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Output<T>(EditorSession session, string id, string port)
    {
        session.SelectedNode = session.Nodes.Single(n => n.NodeId == id);
        return (T)session.SelectedOutputs.Single(o => o.PortName == port).Value;
    }
    private static string Pixels(ImageFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var lease = frame.Lease();
        for (var y = 0; y < frame.Height; y++) hash.AppendData(lease.GetRowSpan(y)[..(frame.Width * frame.PixelFormat.BytesPerPixel())]);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long CountNonzero(ImageFrame image)
    {
        using var lease = image.Lease(); long count = 0;
        for (var y = 0; y < image.Height; y++) foreach (var pixel in lease.GetRowSpan(y)[..image.Width]) if (pixel != 0) count++;
        return count;
    }

    private static async Task Verify(string root)
    {
        var evidence = Path.Combine(root, "artifacts", "phase3a-acceptance"); Directory.CreateDirectory(evidence);
        var samples = Path.Combine(root, "samples", "phase3a"); Directory.CreateDirectory(samples);
        DemoProjectFactory.CreateResources(samples);
        using (var image = new Mat(120, 160, MatType.CV_8UC1, new Scalar(20)))
        {
            Cv2.Rectangle(image, new Rect(20, 20, 20, 16), Scalar.All(230), -1);
            Cv2.Rectangle(image, new Rect(70, 30, 14, 12), Scalar.All(230), -1);
            Cv2.Rectangle(image, new Rect(130, 80, 3, 3), Scalar.All(230), -1);
            Cv2.ImWrite(Path.Combine(samples, "segmentation.png"), image);
        }
        var matcher = new CvInspectTemplateMatcher();
        var registry = BuiltinRegistration.CreateRegistry();
        var store = new ProjectStore(registry, new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
        var flow1 = new ProjectDocument { ProjectName = "A1-真实Blob筛选", Nodes = [
            Node("input", "ImageInput", new ImageInputParameters { Path = "segmentation.png" }),
            Node("threshold", "Threshold", new { ThresholdValue = 128, MaxValue = 255, ThresholdType = "Binary" }),
            Node("morph", "Morphology", new { Operation = "Open", KernelWidth = 3, KernelHeight = 3, KernelShape = "Rectangle", Iterations = 1 }),
            Node("blob", "BlobDetection", new { Threshold = 180, MinArea = 1, MaxArea = 10000 }),
            Node("filter", "BlobFilter", new { Area = new { Enabled = true, Minimum = 100, Maximum = 1000 } }), Node("display", "ImageDisplay", new { }) ], Connections = [
            new("input", "Image", "threshold", "Image"), new("threshold", "ProcessedImage", "morph", "Image"),
            new("morph", "ProcessedImage", "blob", "Image"), new("blob", "BlobResults", "filter", "BlobResults"),
            new("filter", "FilteredMask", "display", "Image"), new("filter", "FilteredBlobs", "display", "Blobs") ] };
        var original = DemoProjectFactory.Create(false);
        var flow2 = original with { ProjectName = "A2-模板姿态旋转裁切", Nodes = [original.Nodes[0], original.Nodes.Single(n => n.NodeId == "match"),
            Node("crop", "RotatedCrop", new { Width = 72, Height = 64 }), Node("display", "ImageDisplay", new { }) ], Connections = [
            new("input", "Image", "match", "Image"), new("input", "Image", "crop", "Image"),
            new("match", "Pose2D", "crop", "Pose2D"), new("crop", "CroppedImage", "display", "Image") ], ROI = [] };
        var flow3 = new ProjectDocument { ProjectName = "A3-中值分割文本结果", Nodes = [
            Node("input", "ImageInput", new ImageInputParameters { Path = "segmentation.png" }), Node("median", "MedianBlur", new { KernelSize = 3 }),
            Node("threshold", "Threshold", new { ThresholdValue = 128, MaxValue = 255, ThresholdType = "Binary" }), Node("text", "TextDisplay", new { }) ], Connections = [
            new("input", "Image", "median", "Image"), new("median", "ProcessedImage", "threshold", "Image"), new("threshold", "ForegroundPixelCount", "text", "Integer") ] };
        var paths = new[] { "flow1.json", "flow2.json", "flow3.json" };
        var projects = new[] { flow1, flow2, flow3 };
        for (var i = 0; i < paths.Length; i++) await store.SaveAsync(Path.Combine(samples, paths[i]), projects[i]);

        var before = FrameHandle.ActiveBytes; var rows = new List<object>();
        var session = new EditorSession(); var window = new MainWindow(session); window.Show();
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        for (var i = 0; i < paths.Length; i++)
        {
            var projectPath = Path.Combine(samples, paths[i]);
            await session.OpenAsync(projectPath); await session.RunAsync();
            Check(session.Results.Count == projects[i].Nodes.Count && session.Results.All(n => n.Status == "Success"), "Flow failed: " + paths[i] + ": " + session.StatusMessage);
            var finalId = i == 2 ? "text" : "display";
            var actual = i == 2 ? Output<string>(session, finalId, "Text") : Pixels(Output<ImageFrame>(session, finalId, "Image"));
            if (i == 0)
            {
                var filtered = Output<BlobResults>(session, "filter", "FilteredBlobs");
                var mask = Output<ImageFrame>(session, "filter", "FilteredMask");
                var nonzero = CountNonzero(mask);
                Check(nonzero == filtered.Items.Sum(b => b.Area), "Mask pixels are not the actual selected foreground.");
                Check(filtered.Count == 2, "Area filter did not remove the small third object.");
            }
            if (i == 1)
            {
                var pose = Output<Pose2D>(session, "match", "Pose2D"); var crop = Output<ImageFrame>(session, "crop", "CroppedImage");
                var transform = Output<CoordinateTransform2D>(session, "crop", "CoordinateTransform");
                var center = transform.Map(new((crop.Width - 1) / 2.0, (crop.Height - 1) / 2.0));
                Check(Math.Abs(center.X - pose.CenterX) < 1e-8 && Math.Abs(center.Y - pose.CenterY) < 1e-8, "Pose/crop center mapping is wrong.");
            }
            if (i == 2) Check(actual == Output<int>(session, "threshold", "ForegroundPixelCount").ToString(System.Globalization.CultureInfo.InvariantCulture), "Text data is not the current threshold measurement.");
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == finalId);
            window.FitEditorViews(); await viewport.WaitForPreviewAsync();
            ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1;
            // Drain the queued Loaded-priority fit before capturing the real rendered window.
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)window.FindName("ExecutionGrid");
            Check(grid.Columns.All(c => c.ActualWidth >= c.MinWidth), "Result columns are unreadable.");
            SaveWindow(window, Path.Combine(evidence, $"flow{i + 1}.png"));

            // The same saved project executes through the public headless Runtime API.
            using (var run = await Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(projects[i].ToGraph(), samples)))
            {
                Check(run.Success, "Headless Runtime failed.");
                var expected = i == 2 ? run.Nodes[finalId].Output<string>("Text") : Pixels(run.Nodes[finalId].Output<ImageFrame>("Image"));
                Check(actual == expected, "WPF/Runtime output differs.");
            }
            var saved = Path.Combine(samples, $"saved-flow{i + 1}.json"); await session.SaveAsync(saved);
            await session.NewAsync(); Check(session.Results.Count == 0 && session.SelectedOutputs.Count == 0, "Switch retained stale display data.");
            await session.OpenAsync(saved); await session.RunAsync();
            var restored = i == 2 ? Output<string>(session, finalId, "Text") : Pixels(Output<ImageFrame>(session, finalId, "Image"));
            Check(actual == restored, "Reopened output differs.");
            await ConsoleCheck(root, saved, Path.Combine(evidence, $"console{i + 1}"), actual, finalId, i == 2);
            rows.Add(new { Project = paths[i], Passed = true, Output = actual, Nodes = session.Results.Select(n => new { n.NodeId, n.Status, n.Milliseconds }).ToArray(), ConsoleMatchesWpf = true, ReopenedMatches = true });
        }

        // Opening a prior Schema 1 project must still execute without migration.
        var oldProject = Path.Combine(root, "test-results", "phase2", "ui", "interactive.json");
        await session.OpenAsync(oldProject); await session.RunAsync();
        Check(session.Results.All(n => n.Status == "Success") && session.Nodes.Count == 2, "Phase 2 project compatibility failed.");
        var largeImage = await LargeImageCheck(session, viewport, evidence);
        await window.CloseEditorAsync(true);
        Check(FrameHandle.ActiveBytes == before, "Closing the window retained image buffers.");
        var summary = new { Passed = true, SyntheticData = true, Flows = rows, OldSchema1Opened = true, BuffersBefore = before, BuffersAfter = FrameHandle.ActiveBytes,
            LargeImage = largeImage, ProcessAfterClose = ProcessMemory(), RegisteredTypes = registry.RegisteredTypeIds.Order().ToArray() };
        File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(summary, JsonDefaults.Options));
        System.Console.WriteLine(JsonSerializer.Serialize(summary, JsonDefaults.Options));
    }

    private static async Task<object> LargeImageCheck(EditorSession session, ImageViewportControl viewport, string evidence)
    {
        await session.NewAsync();
        var path = Path.Combine(evidence, "large-8192x10000.bmp");
        await Task.Run(() =>
        {
            using var image = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8).Build();
            ImageFile.Save(image, path);
        });
        var before = ProcessMemory(); var watch = Stopwatch.StartNew(); var ticks = 0;
        var last = watch.Elapsed.TotalMilliseconds; var maxGap = 0.0;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { ticks++; var now = watch.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - last); last = now; };
        timer.Start();
        await session.ImportImageAsync(path, PixelFormat.Mono8); await viewport.WaitForPreviewAsync();
        viewport.ShowOneToOne(); var point = new Point2D(4100.25, 4500.75);
        viewport.ViewTransform.ZoomAt(viewport.ImageToScreen(point), 1.5); viewport.ViewTransform.Pan(20, -15);
        var restored = viewport.ScreenToImage(viewport.ImageToScreen(point));
        timer.Stop();
        Check(Math.Abs(restored.X - point.X) < 1e-6 && Math.Abs(restored.Y - point.Y) < 1e-6, "82MP display changed pixel coordinates.");
        Check(ticks > 0 && viewport.PreviewBytes <= viewport.MaxPreviewBytes && viewport.PreviewHeight <= 2048, "82MP preview blocked the UI or exceeded cache budget.");
        return new { Width = 8192, Height = 10000, PreviewWidth = viewport.PreviewWidth, PreviewHeight = viewport.PreviewHeight,
            PreviewBytes = viewport.PreviewBytes, CacheBudget = viewport.MaxPreviewBytes, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            DispatcherTicks = ticks, MaximumHeartbeatGapMilliseconds = maxGap, ProcessBefore = before, ProcessWithPreview = ProcessMemory() };
    }

    private static object ProcessMemory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64 };
    }

    private static async Task ConsoleCheck(string root, string project, string directory, string actual, string finalId, bool text)
    {
        var exe = Path.Combine(root, "src", "VisionPlatform.Console", "bin", "Release", "net10.0", "VisionPlatform.Console.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", project, "--outputs", directory }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "console.log"), await stdout + await stderr);
        Check(process.ExitCode == 0, "Independent Console process failed.");
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results.json")));
        var outputs = result.RootElement.GetProperty("Nodes").GetProperty(finalId).GetProperty("Outputs");
        if (text) Check(outputs.GetProperty("Text").GetString() == actual, "Console text differs from WPF.");
        else
        {
            using var image = ImageFile.Load(Path.Combine(directory, outputs.GetProperty("Image").GetProperty("File").GetString()!));
            Check(Pixels(image) == actual, "Independent Console pixel output differs from WPF.");
        }
    }
    private static void SaveWindow(System.Windows.Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}

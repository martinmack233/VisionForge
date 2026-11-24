using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Imaging;
using System.Windows.Controls;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;

internal static class Program
{
    private static string _evidence = "";
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? "."); _evidence = Path.Combine(root, "artifacts/phase4b-acceptance"); Directory.CreateDirectory(_evidence);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        var exit = 1;
        app.Startup += async (_, _) =>
        {
            try { await Verify(root, args.ElementAtOrDefault(1)); exit = 0; }
            catch (Exception e) { Console.Error.WriteLine(e); await Save("summary.json", new { Passed = false, Error = e.ToString() }); }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Check(bool ok, string message) => ExampleRuns.Check(ok, message);
    private static Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(_evidence, name), JsonSerializer.Serialize(value, JsonDefaults.Options));
    private static async Task Verify(string root, string? yoloConfig)
    {
        await Phase4BExampleFactory.SaveAllAsync(Path.Combine(root, "Examples"));
        var registry = BuiltinRegistration.CreateRegistry(); var store = ExampleRuns.Store(registry);
        var catalog = new ExampleCatalog(Path.Combine(root, "Examples"), store); var entries = await catalog.LoadAsync();
        Check(entries.Count == 12 && catalog.LoadErrors.Count == 0, "Expected eight A and four B example manifests.");
        var baselineHashes = await HashFiles(Path.Combine(root, "Examples"));
        foreach (var entry in entries) Check((await catalog.ValidateAsync(entry)).Count == 0, "Example validation: " + entry.Manifest.Id);
        var headless = await ExampleRuns.Headless(catalog, entries.Where(e => e.Manifest.Id != "yolo-external").ToArray(), registry);
        var tcp = await ExampleRuns.Tcp(entries.Single(e => e.Manifest.Id == "tcp-loopback"), registry);
        var large = await LargeRoi(root);
        var baseline = FrameHandle.ActiveBytes;
        var state = Path.Combine(_evidence, "workspace");
        await new WorkspaceStateStore(state).SaveAsync(new(1, new WorkspaceSettings { ExamplesDirectory = Path.Combine(root, "Examples"), DefaultProjectDirectory = Path.Combine(_evidence, "user-projects"), LogDirectory = Path.Combine(_evidence, "logs"), LogEnabled = false }, []));
        var session = new EditorSession(); var window = new MainWindow(session, state); window.Show(); await window.WorkspaceReady;
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        Check(session.Results.Count == 0 && FrameHandle.ActiveBytes == baseline, "Dashboard performed eager inference.");
        var oldProjects = new[] { "tests/Fixtures/legacy-editor-project.json", "samples/phase3a/flow1.json", "samples/phase3a/flow2.json", "samples/phase3a/flow3.json", "samples/phase3b/flow1.json", "samples/phase3c/flow6-enhance-transform.json" };
        foreach (var old in oldProjects)
        {
            await window.OpenProjectAsync(Path.Combine(root, old), discardChanges: true); await session.RunAsync();
            Check(session.Results.Count == session.Nodes.Count && session.Results.All(r => r.Status is "Success" or "Skipped"), "Old project failed: " + old);
        }
        var desktop = new List<object>();
        foreach (var id in new[] { "ocr", "qr-code", "barcode" })
        {
            window.Navigate(WorkspacePage.Examples); window.Examples.Selected = window.Examples.Items.Single(e => e.Manifest.Id == id);
            window.Examples.SelectedCase = window.Examples.Cases.First(c => c.Id == "normal");
            await window.Examples.RunSelectedAsync();
            Check(window.Examples.LastVerification?.Passed == true, id + ": " + window.Examples.Status + window.Examples.LastFailureDetails);
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "recognize");
            var text = (string)session.CurrentOutputs["recognize"]["Text"]; var overlays = session.RecognitionOverlays.Count;
            Check(overlays > 0 && session.InspectOutput("recognize", "Result"), "Recognition result/overlay missing.");
            window.FitEditorViews(); await viewport.WaitForDisplayAsync(); await Capture(window, id + ".png");
            ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1; await Capture(window, id + "-results.png");
            var path = session.ProjectPath!; await session.SaveAsync(); await session.OpenAsync(path); await session.RunAsync();
            Check((string)session.CurrentOutputs["recognize"]["Text"] == text, "Saved project changed recognition.");
            await ExampleRuns.ConsoleRun(root, path, Path.Combine(_evidence, "console", id));
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "recognize"); await session.RunAsync(EditorRunMode.Single);
            Check((string)session.CurrentOutputs["recognize"]["Text"] == text, "Single-node snapshot differed.");
            var input = session.Nodes.Single(n => n.NodeId == "input"); session.CommitParameters(input, JsonDefaults.Element(new ImageInputParameters { Path = "images/blank.png" }));
            Check(session.RecognitionOverlays.Count == 0 && session.CurrentOutputs.Count == 0, "Parameter edit retained stale recognition.");
            await session.RunAsync(); Check((string)session.CurrentOutputs["recognize"]["Text"] == "" && !(bool)session.CurrentOutputs["recognize"]["Success"] && session.RecognitionOverlays.Count == 0, "Blank reused old result.");
            await session.SaveAsync();
            desktop.Add(new { Id = id, Text = text, OverlayCount = overlays, SaveReload = true, SingleNode = true, BlankInvalidation = true, ConsoleCompared = true });
        }
        object? yolo = null;
        if (yoloConfig is not null)
        {
            var config = JsonSerializer.Deserialize<LiveYolo>(await File.ReadAllTextAsync(yoloConfig), JsonDefaults.Options)!;
            var entry = entries.Single(e => e.Manifest.Id == "yolo-external");
            var project = await store.LoadAsync(entry.ProjectPath);
            project = project with { Nodes = project.Nodes.Select(n => n.NodeId switch
            {
                "input" => n with { Parameters = JsonDefaults.Element(new ImageInputParameters { Path = Path.GetFullPath(config.ImagePath) }) },
                "recognize" => n with { Parameters = JsonDefaults.Element(config.Options) }, _ => n
            }).ToList() };
            var live = Path.Combine(_evidence, "live-yolo.json"); await store.SaveAsync(live, project);
            await session.OpenAsync(live); await session.RunAsync();
            Check(session.CurrentOutputs.TryGetValue("recognize", out var values), string.Join(";", session.Results.Select(r => r.Error)));
            var first = (YoloInferenceResult)values!["Result"]; Check(first.Success && first.Detections.Count > 0, "No real model detections.");
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "recognize"); session.InspectOutput("recognize", "Result");
            window.FitEditorViews(); await viewport.WaitForDisplayAsync(); await Capture(window, "yolo-real.png");
            await session.RunAsync(); var second = (YoloInferenceResult)session.CurrentOutputs["recognize"]["Result"];
            Check(first.Detections.SequenceEqual(second.Detections), "Repeated real YOLO output differed.");
            await ExampleRuns.ConsoleRun(root, live, Path.Combine(_evidence, "console", "yolo-real"));
            var node = session.Nodes.Single(n => n.NodeId == "recognize"); session.CommitParameters(node, JsonDefaults.Element(config.Options with { Tiled = true, TileWidth = 320, TileHeight = 320 })); await session.RunAsync();
            var tiled = (YoloInferenceResult)session.CurrentOutputs["recognize"]["Result"];
            Check(tiled.Success && tiled.TileCount > 1 && tiled.Detections.Count > 0, "Original YOLO Tile inference failed.");
            yolo = new { Real = first, Repeat = second, Tiled = tiled, ConsoleCompared = true, ExternalResourcesNotBundled = true };
            await Save("yolo-real.json", yolo); await session.SaveAsync();
        }
        else Console.WriteLine("YOLO real inference not verified: provide external config as second argument.");
        window.Navigate(WorkspacePage.Examples); window.Examples.Selected = window.Examples.Items.Single(e => e.Manifest.Id == "yolo-external");
        await window.Examples.RunSelectedAsync(); Check(window.Examples.LastVerification?.Passed == false && session.Results.Any(r => r.Status == "Failed" && r.Error.Contains("Python", StringComparison.OrdinalIgnoreCase)), "Missing dependency example did not fail clearly.");
        await Capture(window, "yolo-missing-dependencies.png");
        await session.NewAsync(); await session.CloseAsync(); await window.Examples.StopAsync();
        viewport.Dispose(); await viewport.DrainAsync(); window.Close(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(FrameHandle.ActiveBytes == baseline && viewport.DisplayCacheBytes == 0, "Images/cache remained after close.");
        var afterHashes = await HashFiles(Path.Combine(root, "Examples")); Check(baselineHashes.OrderBy(p => p.Key).SequenceEqual(afterHashes.OrderBy(p => p.Key)), "Built-in example modified by UI.");
        var authoring = new VisionPlatform.Editor.Core.Catalog.NodeAuthoringCatalog();
        await File.WriteAllTextAsync(Path.Combine(root, "docs/第四阶段B节点接口.json"), JsonSerializer.Serialize(registry.RegisteredTypeIds.Order().Select(id => new
        { TypeId = id, authoring.Get(id).DisplayName, authoring.Get(id).Category, Parameters = authoring.Get(id).DefaultParameters,
          Inputs = registry.Create(id).Descriptor.InputPorts.Select(p => new { p.Name, Type = p.DataType.Name, p.Required }),
          Outputs = registry.Create(id).Descriptor.OutputPorts.Select(p => new { p.Name, Type = p.DataType.Name, p.Required }) }), JsonDefaults.Options));
        await Save("summary.json", new { Passed = true, OldProjects = oldProjects, Headless = headless, Desktop = desktop, Tcp = tcp, RealYoloVerified = yolo is not null,
            LargeRoi = large, NewNodes = 4, RegisteredNodes = registry.RegisteredTypeIds.Count, Examples = entries.Count,
            BuffersBefore = baseline, BuffersAfter = FrameHandle.ActiveBytes, CacheAfterClose = viewport.DisplayCacheBytes,
            ConsoleWpfEqual = true, BuiltInExamplesUnchanged = true, ProjectSchema = 1, PhaseCStarted = false });
        Console.WriteLine("Phase4B acceptance passed.");
    }
    private static async Task<object> LargeRoi(string root)
    {
        var baseline = FrameHandle.ActiveBytes; var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.PrivateMemorySize64;
        var watch = Stopwatch.StartNew();
        using (var builder = new ImageFrameBuilder(8192, 10000, VisionPlatform.Contracts.Images.PixelFormat.Mono8))
        {
            for (var y = 0; y < builder.Height; y++) builder.GetRowSpan(y).Fill(255);
            foreach (var (id, x, y) in new[] { ("ocr", 1000, 2000), ("barcode", 6000, 8000) })
            {
                using var sample = ImageFile.Load(Path.Combine(root, "Examples", id, "images/input.png")); using var lease = sample.Lease();
                for (var row = 0; row < sample.Height; row++) lease.GetRowSpan(row).CopyTo(builder.GetRowSpan(y + row).Slice(x, sample.Width));
            }
            using var image = builder.Build(); var allocationMs = watch.Elapsed.TotalMilliseconds;
            var executor = new VisionPlatform.Runtime.Execution.RuntimeExecutor(BuiltinRegistration.CreateRegistry());
            var ocrSpec = new NodeSpecification("ocr", "OCR", "ocr", JsonDefaults.Element(new OcrOptions { UseRoi = true, RoiX = 1000, RoiY = 2000, RoiWidth = 640, RoiHeight = 160 }));
            using var ocrRun = await executor.ExecuteSingleAsync(ocrSpec, new Dictionary<string, object> { ["Image"] = image }, root);
            var ocr = ocrRun.Nodes["ocr"].Output<OcrResult>("Result"); Check(ocr.Success && ocr.Text == "VISION 12345" && ocr.Words.All(w => w.Bounds.X >= 1000 && w.Bounds.Y >= 2000), "82 MP OCR ROI failed or used local coordinates.");
            var barcodeSpec = new NodeSpecification("barcode", "BarcodeReader", "barcode", JsonDefaults.Element(new BarcodeReadOptions { UseRoi = true, RoiX = 6000, RoiY = 8000, RoiWidth = 640, RoiHeight = 200 }));
            using var barcodeRun = await executor.ExecuteSingleAsync(barcodeSpec, new Dictionary<string, object> { ["Image"] = image }, root);
            var code = barcodeRun.Nodes["barcode"].Output<CodeReadResult>("Result"); Check(code.Success && code.Text == "VP12345678" && code.Codes.All(c => c.Points.All(p => p.X >= 6000 && p.Y >= 8000)), "82 MP barcode ROI failed or used local coordinates.");
            Check(FrameHandle.ActiveBytes == baseline + 81920000, "Recognition cloned the entire 82 MP image into another platform frame.");
            process.Refresh(); await Save("large-roi.json", new { Width = image.Width, Height = image.Height, Format = image.PixelFormat.ToString(), AllocationMilliseconds = allocationMs, OcrMilliseconds = ocrRun.ExecutionTime.TotalMilliseconds, BarcodeMilliseconds = barcodeRun.ExecutionTime.TotalMilliseconds, PlatformImageBytes = FrameHandle.ActiveBytes - baseline, PrivateBytesDelta = process.PrivateMemorySize64 - before, OriginalCoordinateChecks = true, Ocr = ocr, Barcode = code });
        }
        Check(FrameHandle.ActiveBytes == baseline, "82 MP ROI test leaked an image reference.");
        return new { Passed = true, Width = 8192, Height = 10000, SourceBytes = 81920000, BuffersBefore = baseline, BuffersAfter = FrameHandle.ActiveBytes };
    }
    private static async Task Capture(Window window, string name)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(_evidence, name)); encoder.Save(stream);
    }
    private static async Task<Dictionary<string, string>> HashFiles(string directory)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { await using var stream = File.OpenRead(file); result[Path.GetRelativePath(directory, file)] = Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
        return result;
    }
    private sealed record LiveYolo(string ImagePath, YoloInferenceOptions Options);
}

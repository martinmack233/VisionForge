using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
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
using PixelFormatConverter = VisionPlatform.Imaging.PixelFormatConverter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var seconds = args.Length > 1 ? int.Parse(args[1]) : 120;
        if (seconds is < 10 or > 1800) throw new ArgumentException("Preview observation must last 10..1800 seconds.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        var exit = 1;
        app.Startup += async (_, _) =>
        {
            try { await Verify(root, seconds); exit = 0; }
            catch (Exception error) { System.Console.Error.WriteLine(error); }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Output<T>(EditorSession session, string id, string port)
    {
        session.SelectedNode = session.Nodes.Single(n => n.NodeId == id);
        return (T)session.SelectedOutputs.Single(o => o.PortName == port).Value;
    }
    private static async Task Stable(ImageViewportControl viewport)
    {
        await viewport.Dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await viewport.WaitForDisplayAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await viewport.Dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await viewport.WaitForTilesAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task Verify(string root, int seconds)
    {
        var samples = Path.Combine(root, "samples/phase3c");
        var evidence = Path.Combine(root, "artifacts/phase3c-acceptance"); Directory.CreateDirectory(evidence);
        System.Console.WriteLine("Generating deterministic samples, including the full 82MP bitmap.");
        await Task.Run(() => Phase3CSampleFactory.SaveAllAsync(samples, includeLargeImage: true));
        var baseline = FrameHandle.ActiveBytes; var registry = BuiltinRegistration.CreateRegistry();
        var catalog = new VisionPlatform.Editor.Core.Catalog.NodeAuthoringCatalog();
        object Port(PortDefinition p) => new { p.Name, Type = p.DataType.Name, p.Required, p.Kind, p.Constraint };
        var manifest = registry.RegisteredTypeIds.Order().Select(id => new { TypeId = id, catalog.Get(id).DisplayName, catalog.Get(id).Category,
            registry.Create(id).Descriptor.Version, Inputs = NodePorts.Inputs(registry.Create(id).Descriptor).Select(Port).ToArray(),
            Outputs = NodePorts.Outputs(registry.Create(id).Descriptor).Select(Port).ToArray(), Parameters = catalog.Get(id).DefaultParameters }).ToArray();
        File.WriteAllText(Path.Combine(root, "docs/第三阶段完整节点接口.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        var store = new ProjectStore(registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
        var session = new EditorSession(); var window = new MainWindow(session); window.Show();
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var measurements = new List<object>();
        foreach (var name in new[] { "flow1-preprocessing.json", "flow2-filter.json", "flow3-matching.json", "flow6-enhance-transform.json" })
        {
            var projectPath = Path.Combine(samples, name); await session.OpenAsync(projectPath);
            await session.RunAsync(); Check(session.Results.All(r => r.Status == "Success"), name + ": " + session.StatusMessage);
            var final = session.Nodes.Last(); session.SelectedNode = final;
            if (session.SelectedOutputs.FirstOrDefault(o => o.Value is ImageFrame) is { } imageOutput)
            {
                session.InspectOutput(final.NodeId, imageOutput.PortName);
                Check(session.SelectedOutputInspection?.DataType == nameof(ImageFrame), "Typed output inspection did not describe the image.");
            }
            await Stable(viewport); await Capture(window, evidence, name.Replace(".json", ".png"));
            var snapshot = session.SnapshotProject(); var rootPath = session.ResourceRoot;
            using (var headless = await Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(snapshot.ToGraph(), rootPath)))
            {
                Check(headless.Success, "Headless failed: " + name);
                foreach (var node in session.Nodes)
                {
                    session.SelectedNode = node;
                    foreach (var output in session.SelectedOutputs)
                    {
                        var expected = headless.Nodes[node.NodeId].Outputs[output.PortName];
                        if (output.Value is ImageFrame image && expected is ImageFrame comparison)
                            Check(PerformanceFixtures.Hash(image) == PerformanceFixtures.Hash(comparison), "Console/WPF pixels differ.");
                        else if (output.Value is string text && expected is string expectedText && output.PortName == "Message" && text.StartsWith('{'))
                        {
                            var actualJson = JsonNode.Parse(text)!.AsObject(); var expectedJson = JsonNode.Parse(expectedText)!.AsObject();
                            // These fields describe different real execution sessions, not algorithm values.
                            foreach (var key in new[] { "inspectionId", "elapsedMs" }) { actualJson.Remove(key); expectedJson.Remove(key); }
                            Check(JsonNode.DeepEquals(actualJson, expectedJson), $"Runtime/WPF structured message differs: {name}/{node.NodeId}.");
                        }
                        else if (output.Value is int or double or bool or string)
                            Check(Equals(output.Value, expected), $"Runtime/WPF scalar differs: {name}/{node.NodeId}/{output.PortName}.");
                    }
                }
            }
            session.SelectedNode = final;
            var saved = Path.Combine(evidence, "saved", name); await session.SaveAsync(saved);
            await session.NewAsync(); await session.OpenAsync(saved); await session.RunAsync();
            Check(session.Results.All(r => r.Status == "Success"), "Saved C graph failed to reopen.");
            var project = await store.LoadAsync(saved); var runs = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                using var result = await Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), Path.GetDirectoryName(saved)!));
                Check(result.Success, "Repeated algorithm run failed.");
                if (i > 0) runs.Add(result.ExecutionTime.TotalMilliseconds);
                if (i == 5) measurements.Add(new { Project = name, WarmupRuns = 1, MeasuredRuns = 5,
                    MedianMilliseconds = PerformanceFixtures.Median(runs), MaximumMilliseconds = runs.Max(),
                    Nodes = result.Nodes.Values.Select(n => new { n.NodeId, n.ExecutionTime, n.Status }).ToArray() });
            }
        }
        var console = await ConsoleCheck(root, Path.Combine(evidence, "saved/flow1-preprocessing.json"), Path.Combine(evidence, "console"));
        var tcp = await ConditionalTcp(root, samples, session, window, viewport, evidence, store);

        var oldProjects = new[] { "tests/Fixtures/legacy-editor-project.json", "samples/phase3a/flow1.json", "samples/phase3a/flow2.json", "samples/phase3a/flow3.json", "samples/phase3b/flow1.json" };
        foreach (var relative in oldProjects)
        {
            await session.OpenAsync(Path.Combine(root, relative)); await session.RunAsync();
            Check(session.Results.All(r => r.Status is "Success" or "Skipped") && session.Results.Any(r => r.Status == "Success"), "Old project failed: " + relative);
        }
        System.Console.WriteLine("C samples and old projects passed. Starting real high-resolution viewport measurements.");
        var display = await DisplayPerformance(session, window, viewport, samples, evidence, seconds);
        var largeRuntime = await LargeRuntimePerformance(samples, registry);
        for (var i = 0; i < 8; i++)
        {
            await session.NewAsync(); await session.OpenAsync(Path.Combine(samples, "flow1-preprocessing.json")); await session.RunAsync(); await Stable(viewport);
            Check(session.Results.All(r => r.Status == "Success"), "Open/close cycle failed.");
        }
        await window.CloseEditorAsync(true);
        Check(viewport.DisplayCacheBytes == 0 && FrameHandle.ActiveBytes == baseline, "Window close retained tiles or image buffers.");
        var summary = new { Passed = true, SyntheticImages = true, SampleGraphsSavedReopened = true, ConsoleMatchesWpf = true,
            OldProjects = oldProjects, Console = console, Tcp = tcp, NodePerformance = measurements, LargeRuntime = largeRuntime, Display = display,
            OpenCloseCycles = 8, BuffersBefore = baseline, BuffersAfter = FrameHandle.ActiveBytes, DisplayCacheAfterClose = viewport.DisplayCacheBytes,
            AfterCloseMemory = PerformanceFixtures.Memory(), FullDayIndustrialStabilityValidated = false };
        File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(summary, JsonDefaults.Options));
        System.Console.WriteLine("PASS: C samples, real raw-pixel tiles, scoped cache, cancellation and resource recovery. " + Path.Combine(evidence, "summary.json"));
    }

    private static async Task<object> DisplayPerformance(EditorSession session, MainWindow window, ImageViewportControl viewport, string samples, string evidence, int seconds)
    {
        var rows = new List<object>(); var ticks = 0; var maximumGap = 0.0; var heartbeat = Stopwatch.StartNew(); var last = heartbeat.Elapsed.TotalMilliseconds;
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { ticks++; var now = heartbeat.Elapsed.TotalMilliseconds; maximumGap = Math.Max(maximumGap, now - last); last = now; };
        timer.Start();
        try
        {
            await session.OpenAsync(Path.Combine(samples, "flow5-large.json"));
            foreach (var format in new[] { PixelFormat.Mono8, PixelFormat.BayerRG8, PixelFormat.BayerGB8, PixelFormat.RGB8, PixelFormat.BGR8 })
            {
                var width = format is PixelFormat.RGB8 or PixelFormat.BGR8 ? 4096 : 8192;
                var height = format is PixelFormat.RGB8 or PixelFormat.BGR8 ? 5000 : 10000;
                var path = format == PixelFormat.Mono8 ? Path.Combine(samples, "large-8192x10000.bmp") : Path.Combine(evidence, format + ".bmp");
                if (format != PixelFormat.Mono8)
                    await Task.Run(() => { using var generated = PerformanceFixtures.Create(width, height, format); ImageFile.Save(generated, path); });
                var before = PerformanceFixtures.Memory(); var watch = Stopwatch.StartNew();
                await session.ImportImageAsync(path, format); var load = watch.Elapsed.TotalMilliseconds;
                viewport.FitToWindow(); await Stable(viewport); var fit = watch.Elapsed.TotalMilliseconds - load;
                Check(viewport.DisplayCacheBytes <= viewport.MaxDisplayCacheBytes, "Fit cache exceeded budget.");
                using var retained = session.DisplayImage!.Share(); var originalHash = await Task.Run(() => PerformanceFixtures.Hash(retained));
                watch.Restart(); viewport.ShowOneToOne(); await Stable(viewport); var oneToOne = watch.Elapsed.TotalMilliseconds;
                Check(viewport.IsNativeResolutionReady && viewport.CurrentPyramidLevel == 0, "1:1 is still an enlarged thumbnail.");
                Check(viewport.VisibleImageRegion!.Width < width && viewport.VisibleImageRegion.Height < height, "1:1 requested the whole high-resolution raster.");
                var compared = ValidateRawTiles(viewport, retained);
                var point = new Point2D(width / 2.0 + .25, height / 2.0 + .75);
                watch.Restart(); viewport.ViewTransform.Pan(31, -17); viewport.ViewTransform.ZoomAt(viewport.ImageToScreen(point), 1.25); await Stable(viewport);
                var inverse = viewport.ScreenToImage(viewport.ImageToScreen(point));
                Check(Math.Abs(inverse.X - point.X) < 1e-6 && Math.Abs(inverse.Y - point.Y) < 1e-6, "Zoom/pan changed detection coordinates.");
                var interaction = watch.Elapsed.TotalMilliseconds;
                session.UpdateRoi(new(width / 2.0, height / 2.0, 96, 64, 17)); await Stable(viewport);
                Check(session.EditedRoi?.AngleDegrees == 17 && session.SnapshotProject().ROI.Any(r => r.AngleDegrees == 17), "Existing ROI did not retain original coordinates.");
                Check(originalHash == await Task.Run(() => PerformanceFixtures.Hash(retained)), "Display changed source pixels.");
                Check(viewport.DisplayCacheBytes <= viewport.MaxDisplayCacheBytes, "Pan cache exceeded budget.");
                rows.Add(new { Format = format, Width = width, Height = height, LoadMilliseconds = load, FitPreviewMilliseconds = fit,
                    NativeOneToOneMilliseconds = oneToOne, PanZoomMilliseconds = interaction, RawPixelsCompared = compared,
                    viewport.CurrentPyramidLevel, viewport.VisibleImageRegion, VisibleTiles = viewport.VisibleTiles.Count,
                    viewport.DisplayCacheBytes, viewport.MaxDisplayCacheBytes, Before = before, WithPreview = PerformanceFixtures.Memory() });
                System.Console.WriteLine($"{format} {width}x{height}: load {load:F1} ms, true 1:1 {oneToOne:F1} ms, {compared} original samples checked.");
                if (format is PixelFormat.Mono8 or PixelFormat.BayerRG8)
                {
                    viewport.ShowOneToOne(); await Stable(viewport);
                    await Capture(window, evidence, format == PixelFormat.Mono8 ? "native-mono-1to1.png" : "native-bayer-1to1.png", fit: false);
                }
            }
            // Rapid source replacement explicitly leaves old work to cancellation/Drain.
            for (var i = 0; i < 12; i++)
            {
                using var image = await Task.Run(() => PerformanceFixtures.Create(2048, 1536, i % 2 == 0 ? PixelFormat.BGR8 : PixelFormat.BayerGB8));
                viewport.SetCurrentValue(ImageViewportControl.SourceImageProperty, image); viewport.ShowOneToOne(); viewport.ViewTransform.Pan(i * 23, -i * 11);
            }
            await Stable(viewport); await viewport.DrainAsync();
            // Rebind the session-owned frame after the rapid-swap stress.
            viewport.SetCurrentValue(ImageViewportControl.SourceImageProperty, session.DisplayImage); viewport.ShowOneToOne(); await Stable(viewport);
            var observations = new List<object>(); var soak = Stopwatch.StartNew();
            while (soak.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(1000);
                if ((int)soak.Elapsed.TotalSeconds % 10 == 0)
                {
                    viewport.ViewTransform.Pan(17, -9); await Stable(viewport);
                    Check(viewport.DisplayCacheBytes <= viewport.MaxDisplayCacheBytes, "Held preview cache grew past its bound.");
                    observations.Add(new { Seconds = soak.Elapsed.TotalSeconds, viewport.DisplayCacheBytes, Memory = PerformanceFixtures.Memory() });
                    System.Console.WriteLine($"Preview observation {soak.Elapsed.TotalSeconds:F0}/{seconds}s, cache={viewport.DisplayCacheBytes:N0} bytes.");
                }
            }
            return new { Formats = rows, RapidSwitches = 12, HeldPreviewSeconds = soak.Elapsed.TotalSeconds, Observations = observations,
                DispatcherTicks = ticks, MaximumHeartbeatGapMilliseconds = maximumGap, viewport.CancelledTileBatches,
                viewport.TileRequests, viewport.TileCacheHits, PixelDataUnchanged = true };
        }
        finally { timer.Stop(); }
    }

    private static long ValidateRawTiles(ImageViewportControl viewport, ImageFrame source)
    {
        long compared = 0; using var raw = source.Lease();
        foreach (var tile in viewport.VisibleTiles)
        {
            Check(tile.PyramidLevel == 0, "Non-native tile at 1:1."); var bitmap = tile.Preview.Bitmap;
            var channels = source.PixelFormat == PixelFormat.Mono8 ? 1 : 3; var stride = bitmap.PixelWidth * channels;
            var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
            var x0 = (int)(tile.Region.X - source.CoordinateSpace.OriginX); var y0 = (int)(tile.Region.Y - source.CoordinateSpace.OriginY);
            foreach (var y in new[] { 0, 1, bitmap.PixelHeight / 2, bitmap.PixelHeight - 1 }.Distinct().Where(y => y >= 0 && y < bitmap.PixelHeight))
                foreach (var x in new[] { 0, 1, bitmap.PixelWidth / 2, bitmap.PixelWidth - 1 }.Distinct().Where(x => x >= 0 && x < bitmap.PixelWidth))
                {
                    var offset = y * stride + x * channels; var sourceRow = raw.GetRowSpan(y0 + y);
                    if (source.PixelFormat == PixelFormat.Mono8) Check(pixels[offset] == sourceRow[x0 + x], "Native mono tile differs from original pixel.");
                    else if (source.PixelFormat is PixelFormat.BGR8 or PixelFormat.RGB8)
                        for (var c = 0; c < 3; c++) Check(pixels[offset + c] == sourceRow[(x0 + x) * 3 + (source.PixelFormat == PixelFormat.RGB8 ? 2 - c : c)], "Native colour tile channel differs.");
                    else
                    {
                        var left = Math.Max(0, x0 + x - 4); var top = Math.Max(0, y0 + y - 4);
                        using var view = source.CreateView(left, top, Math.Min(9, source.Width - left), Math.Min(9, source.Height - top));
                        using var converted = PixelFormatConverter.Convert(view, PixelFormat.BGR8); using var expected = converted.Image.Lease();
                        var row = expected.GetRowSpan(y0 + y - top);
                        for (var c = 0; c < 3; c++) Check(pixels[offset + c] == row[(x0 + x - left) * 3 + c], "Bayer tile halo/phase differs from independent bilinear reference.");
                    }
                    compared++;
                }
        }
        Check(compared > 0, "No original tile samples were compared."); return compared;
    }

    private static async Task<object> LargeRuntimePerformance(string samples, VisionPlatform.Runtime.Graph.NodeRegistry registry)
    {
        var nodes = new[] { new NodeSpecification("input", "ImageInput", "ImageInput", JsonDefaults.Element(new { Path = "large-8192x10000.bmp" })),
            new("normalize", "Normalize", "Normalize", JsonDefaults.Element(new NormalizeOptions())), new("blur", "GaussianBlur", "GaussianBlur", JsonDefaults.Element(new GaussianBlurParameters())),
            new("otsu", "OtsuThreshold", "OtsuThreshold", JsonDefaults.Element(new OtsuThresholdOptions())), new("morph", "Morphology", "Morphology", JsonDefaults.Element(new MorphologyOptions())) };
        var graph = new VisionPlatform.Runtime.Graph.NodeGraph(nodes, [new("input", "Image", "normalize", "Image"), new("normalize", "ProcessedImage", "blur", "Image"),
            new("blur", "ProcessedImage", "otsu", "Image"), new("otsu", "BinaryImage", "morph", "Image")]);
        var runs = new List<double>(); object? measured = null; var before = FrameHandle.ActiveBytes;
        for (var i = 0; i < 4; i++)
        {
            using var result = await Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(graph, samples));
            Check(result.Success, "82MP processing chain failed."); if (i > 0) runs.Add(result.ExecutionTime.TotalMilliseconds);
            measured = new { Nodes = result.Nodes.Values.Select(n => new { n.NodeId, n.ExecutionTime, n.Status }).ToArray(), WithOutputs = PerformanceFixtures.Memory() };
        }
        Check(FrameHandle.ActiveBytes == before, "Repeated 82MP chain leaked frames.");
        using var cancellation = new CancellationTokenSource(); var cancelWatch = Stopwatch.StartNew();
        var pending = Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(graph, samples, cancellation.Token));
        await Task.Delay(20); cancellation.Cancel();
        using (var cancelled = await pending) Check(cancelled.Status == NodeExecutionStatus.Cancelled, "Active large run did not report cancellation.");
        Check(FrameHandle.ActiveBytes == before, "Cancelled large chain retained frames after disposal.");
        return new { Width = 8192, Height = 10000, WarmupRuns = 1, MeasuredRuns = 3, MedianMilliseconds = PerformanceFixtures.Median(runs), MaximumMilliseconds = runs.Max(), LastRun = measured,
            CancelledRunReleased = true, ActiveCancellationMilliseconds = cancelWatch.Elapsed.TotalMilliseconds };
    }

    private static async Task<object> ConditionalTcp(string root, string samples, EditorSession session, MainWindow window, ImageViewportControl viewport, string evidence, ProjectStore store)
    {
        var info = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var peerFile = Path.Combine(evidence, "tcp.json");
        foreach (var arg in new[] { "-u", Path.Combine(root, "tools/tcp_fixture.py"), "--port", "0", "--output", peerFile, "--linger", "0.8" }) info.ArgumentList.Add(arg);
        using var peer = Process.Start(info)!;
        var ready = await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var header = JsonDocument.Parse(ready!); var port = header.RootElement.GetProperty("port").GetInt32();
        var original = await store.LoadAsync(Path.Combine(samples, "flow4-conditional-tcp.json"));
        var project = original with { Nodes = original.Nodes.Select(n => n.NodeTypeId == "TcpConnect" ? n with { Parameters = JsonDefaults.Element(n.Parameters.Deserialize<TcpConnectParameters>(JsonDefaults.Options)! with
            { Options = n.Parameters.Deserialize<TcpConnectParameters>(JsonDefaults.Options)!.Options with { Port = port } }) } : n.NodeTypeId == "ImageInput" ? n with { Parameters = JsonDefaults.Element(n.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)! with
            { Path = ProjectStore.ResolveResourcePath(samples, n.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!.Path) }) } : n).ToList() };
        var path = Path.Combine(evidence, "tcp-project.json"); await store.SaveAsync(path, project); await session.OpenAsync(path); await session.RunAsync(); await session.RunAsync();
        Check(session.Results.Count(r => r.Status == "Skipped") >= 1, "Conditional communication did not skip a branch.");
        var report = session.Nodes.LastOrDefault(n => n.TypeId == "ResultDisplay"); if (report is not null) session.SelectedNode = report;
        await Stable(viewport); await Capture(window, evidence, "conditional-tcp.png");
        await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(peer.ExitCode == 0, "C TCP peer found duplicate or missing messages.");
        File.WriteAllText(Path.Combine(evidence, "tcp-peer.log"), ready + "\n" + await peer.StandardOutput.ReadToEndAsync() + await peer.StandardError.ReadToEndAsync());
        using var received = JsonDocument.Parse(File.ReadAllText(peerFile)); var messages = received.RootElement.GetProperty("messages");
        Check(messages.GetArrayLength() == 1, "Unselected/repeated C branch sent data."); return new { Passed = true, Port = port, Message = messages[0].GetString(), OnlyOneMessage = true };
    }

    private static async Task<object> ConsoleCheck(string root, string project, string output)
    {
        var exe = Path.Combine(root, "src/VisionPlatform.Console/bin/Release/net10.0/VisionPlatform.Console.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", project, "--outputs", output }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "console.log"), await stdout + await stderr); Check(process.ExitCode == 0, "C project failed in independent Console process.");
        var registry = BuiltinRegistration.CreateRegistry();
        var store = new ProjectStore(registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
        var document = await store.LoadAsync(project);
        using var expected = await Task.Run(() => new RuntimeExecutor(registry).ExecuteAsync(document.ToGraph(), Path.GetDirectoryName(project)!));
        using var exported = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "results.json")));
        var nodes = exported.RootElement.GetProperty("Nodes"); var images = 0; var scalars = 0;
        foreach (var node in expected.Nodes.Values)
        {
            var actual = nodes.GetProperty(node.NodeId);
            Check(actual.GetProperty("Status").GetString() == node.Status.ToString(), "Console node status differs.");
            foreach (var (port, value) in node.Outputs)
            {
                var data = actual.GetProperty("Outputs").GetProperty(port);
                if (value is ImageFrame image)
                {
                    using var pixels = ImageFile.Load(Path.Combine(output, data.GetProperty("File").GetString()!), image.PixelFormat);
                    Check(PerformanceFixtures.Hash(pixels) == PerformanceFixtures.Hash(image), "Independent Console exported different image pixels."); images++;
                }
                else if (value is int or double or bool or string)
                {
                    Check(JsonNode.DeepEquals(JsonNode.Parse(data.GetRawText()), JsonSerializer.SerializeToNode(value, JsonDefaults.Options)), "Independent Console scalar differs."); scalars++;
                }
            }
        }
        Check(images > 0 && scalars > 0, "Console comparison did not check actual image and numeric outputs.");
        return new { Passed = true, IndependentProcess = true, ImagesComparedByHash = images, ScalarsCompared = scalars };
    }
    private static async Task Capture(MainWindow window, string directory, string name, bool fit = true)
    {
        if (fit) window.FitEditorViews(); ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}

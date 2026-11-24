using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Editor.Wpf.Parameters;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        var exit = 1;
        app.Startup += async (_, _) =>
        {
            try { await Verify(root); exit = 0; }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                var evidence = Path.Combine(root, "artifacts/phase4a-acceptance"); Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new { Passed = false, Error = e.ToString() }, JsonDefaults.Options));
            }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static void Check(bool condition, string message) => ExampleRuns.Check(condition, message);
    private static async Task Verify(string root)
    {
        var evidence = Path.Combine(root, "artifacts/phase4a-acceptance"); Directory.CreateDirectory(evidence);
        var examplesRoot = Path.Combine(root, "Examples");
        await Phase4AExampleFactory.SaveAllAsync(examplesRoot);
        var registry = BuiltinRegistration.CreateRegistry(); var store = ExampleRuns.Store(registry);
        var authoring = new VisionPlatform.Editor.Core.Catalog.NodeAuthoringCatalog();
        object Port(PortDefinition port) => new { port.Name, Type = port.DataType.Name, port.Required, port.Kind, port.Constraint };
        var interfaces = registry.RegisteredTypeIds.Order().Select(id => new { TypeId = id, authoring.Get(id).DisplayName,
            authoring.Get(id).Category, registry.Create(id).Descriptor.Version, Parameters = authoring.Get(id).DefaultParameters,
            Inputs = NodePorts.Inputs(registry.Create(id).Descriptor).Select(Port).ToArray(),
            Outputs = NodePorts.Outputs(registry.Create(id).Descriptor).Select(Port).ToArray() }).ToArray();
        await File.WriteAllTextAsync(Path.Combine(root, "docs/第四阶段A节点接口.json"), JsonSerializer.Serialize(interfaces, JsonDefaults.Options));
        var catalog = new ExampleCatalog(examplesRoot, store); var entries = await catalog.LoadAsync();
        Check(entries.Count == 8 && catalog.LoadErrors.Count == 0, "Expected eight complete example manifests.");
        var originalHashes = await HashFiles(examplesRoot);
        var headless = await ExampleRuns.Headless(catalog, entries, registry);
        var tcp = await ExampleRuns.Tcp(entries.Single(e => e.Manifest.Id == "tcp-loopback"), registry);
        var baseline = FrameHandle.ActiveBytes;
        var state = Path.Combine(evidence, "workspace-state"); var userProjects = Path.Combine(evidence, "user-projects");
        await new WorkspaceStateStore(state).SaveAsync(new(1, new WorkspaceSettings { ExamplesDirectory = examplesRoot,
            DefaultProjectDirectory = userProjects, LogDirectory = Path.Combine(evidence, "logs"), LogEnabled = false }, []));
        var session = new EditorSession(); var window = new MainWindow(session, state); window.Show();
        await window.WorkspaceReady;
        Check(window.Workspace.CurrentPage == WorkspacePage.Dashboard && session.Results.Count == 0 && FrameHandle.ActiveBytes == baseline,
            "Dashboard loaded unnecessary image data or ran a graph.");
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        await Capture(window, evidence, "dashboard.png");
        window.Workspace.ToggleNavigationCommand.Execute(null); Check(!window.Workspace.NavigationExpanded, "Navigation did not collapse.");
        await Capture(window, evidence, "navigation-collapsed.png");
        window.Workspace.ToggleNavigationCommand.Execute(null);
        foreach (var page in Enum.GetValues<WorkspacePage>()) { window.Navigate(page); await Idle(window); }
        Check(FrameHandle.ActiveBytes == baseline && session.Results.Count == 0, "Page changes executed or allocated an image.");
        window.Navigate(WorkspacePage.Examples);
        var examples = window.Examples;
        examples.Search = "卡尺亚像素"; Check(examples.VisibleItems.Count == 1 && examples.VisibleItems[0].Manifest.Id == "caliper", "Chinese example search failed."); examples.Search = "";
        examples.Category = "几何测量"; Check(examples.VisibleItems.Count == 2, "Example category filter failed."); examples.Category = "全部";
        await Capture(window, evidence, "examples.png");
        examples.Selected = examples.Items.Single(e => e.Manifest.Id == "basic"); await examples.OpenSelectedAsync();
        Check(session.DisplayImage is null && session.CurrentOutputs.Count == 0 && session.Nodes.Count == 4 && FrameHandle.ActiveBytes == baseline,
            "Opening a flow preview loaded full image pixels or ran algorithms before an explicit run.");
        await Capture(window, evidence, "configuration-preview.png");
        var desktop = new List<object>();
        foreach (var entry in entries)
        {
            examples.Selected = examples.Items.Single(e => e.Manifest.Id == entry.Manifest.Id);
            await examples.RunSelectedAsync(entry.Manifest.RequiresExplicitCommunication);
            Check(examples.LastVerification?.Passed == true, entry.Manifest.Id + " WPF: " + examples.Status + "\n" + examples.LastFailureDetails);
            Check(session.ProjectPath is not null && !session.ProjectPath.StartsWith(examplesRoot, StringComparison.OrdinalIgnoreCase), "Example template was edited directly.");
            Check(window.Workspace.RecentProjects.Any(p => p.Path == session.ProjectPath), "Opened example copy was not recorded as a recent project.");
            var project = session.SnapshotProject();
            if (!entry.Manifest.RequiresExplicitCommunication)
            {
                using var reference = await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), session.ResourceRoot);
                CompareOutputs(session.CurrentOutputs, reference);
            }
            if (entry.Manifest.Id is "caliper" or "circle")
                Check(session.MeasurementOverlays.Count > 0 && viewport.MeasurementOverlays?.Count > 0, "Measurement overlay missing from the actual image window.");
            window.FitEditorViews(); await Stable(viewport);
            if (entry.Manifest.Id == "circle")
            {
                session.InspectOutput("circle", "Result");
                Check(session.SelectedOutputInspection?.Fields.Any(f => f.Name.Contains("Radius", StringComparison.OrdinalIgnoreCase) || f.Value.Contains("60")) == true,
                    "Circle detail panel omitted the actual measurement.");
            }
            await Capture(window, evidence, entry.Manifest.Id + ".png");
            desktop.Add(new { entry.Manifest.Id, Passed = examples.LastVerification!.Passed, Project = session.ProjectPath,
                Nodes = session.Results.ToArray(), MeasurementOverlays = session.MeasurementOverlays.Count,
                ActualMeasurements = session.CurrentOutputs.Values.SelectMany(v => v.Values).OfType<MeasurementResult>().Select(m => m.Summary).ToArray() });
            if (entry.Manifest.Id is "basic" or "caliper" or "circle")
                await ExampleRuns.ConsoleRun(root, session.ProjectPath!, Path.Combine(evidence, "console", entry.Manifest.Id));
            // Copies are user projects. Saving those avoids a discard prompt on the next example.
            if (session.HasChanges) await session.SaveAsync();
        }
        // Formal parameters and an unapplied parameter draft survive ordinary navigation.
        examples.Selected = examples.Items.Single(e => e.Manifest.Id == "basic"); await examples.RunSelectedAsync();
        session.SelectedNode = session.Nodes.Single(n => n.TypeId == "Threshold");
        var panel = (ParameterPanel)window.FindName("ParametersView"); var draft = panel.Draft!;
        var field = draft.Fields.Single(f => f.Descriptor.Path == "ThresholdValue"); field.Text = "132";
        var displayed = session.DisplayImage; var bytes = FrameHandle.ActiveBytes; var revision = session.Revision;
        foreach (var page in Enum.GetValues<WorkspacePage>()) { window.Navigate(page); await Idle(window); }
        window.Navigate(WorkspacePage.Editor); await Idle(window);
        Check(ReferenceEquals(panel.Draft, draft) && field.Text == "132" && session.Revision == revision && ReferenceEquals(session.DisplayImage, displayed) && FrameHandle.ActiveBytes == bytes,
            "Navigation lost parameter drafts or released a borrowed image.");
        // Applying a draft clears all stale typed results and display metadata.
        Check(panel.ApplyChanges(), "Threshold draft failed validation.");
        Check(session.CurrentOutputs.Count == 0 && session.MeasurementOverlays.Count == 0 && session.SelectedOutputInspection is null, "Parameter change retained a stale result.");
        var pending = session.RunAsync(); window.Navigate(WorkspacePage.Monitor); await pending;
        Check(session.Results.All(r => r.Status == "Success") && window.Workspace.RunHistory.Count > 0, "Monitor did not observe the same background Runtime.");
        await Capture(window, evidence, "monitor.png");
        await session.SaveAsync();
        var oldProjects = new[] { "tests/Fixtures/legacy-editor-project.json", "samples/phase3a/flow1.json", "samples/phase3a/flow2.json", "samples/phase3a/flow3.json", "samples/phase3b/flow1.json", "samples/phase3c/flow6-enhance-transform.json" };
        foreach (var old in oldProjects)
        {
            await window.OpenProjectAsync(Path.Combine(root, old), discardChanges: true); await session.RunAsync();
            Check(session.Results.All(r => r.Status is "Success" or "Skipped"), "Old project failed: " + old);
        }
        // 82MP originals remain owned by the session while the editor is hidden.
        await window.OpenProjectAsync(Path.Combine(root, "samples/phase3c/flow5-large.json"), discardChanges: true);
        await Idle(window); await Stable(viewport); viewport.ShowOneToOne(); await Stable(viewport); Check(viewport.IsNativeResolutionReady, "82MP view is not true level zero.");
        session.UpdateRoi(new(4096, 5000, 120, 80, 17)); var roi = session.EditedRoi;
        bytes = FrameHandle.ActiveBytes; displayed = session.DisplayImage;
        window.Navigate(WorkspacePage.Dashboard); await Idle(window); window.Navigate(WorkspacePage.Editor); await Stable(viewport);
        Check(ReferenceEquals(displayed, session.DisplayImage) && FrameHandle.ActiveBytes == bytes && session.EditedRoi == roi, "Navigating away from 82MP image changed image/ROI ownership.");
        var roiCopy = Path.Combine(evidence, "large-roi.json"); await session.SaveAsync(roiCopy);
        await session.NewAsync(); await window.OpenProjectAsync(roiCopy, true); Check(session.EditedRoi == roi, "Original ROI did not survive save/reload.");
        await Idle(window); await Stable(viewport); viewport.ShowOneToOne(); await Stable(viewport); await Capture(window, evidence, "large-roi.png");
        for (var i = 0; i < 5; i++)
        {
            await window.OpenProjectAsync(Path.Combine(root, "samples/phase3c/flow1-preprocessing.json"), true);
            await session.RunAsync(); await Stable(viewport); Check(session.Results.All(r => r.Status == "Success"), "Repeated project switching failed.");
        }
        await window.CloseEditorAsync(true);
        Check(FrameHandle.ActiveBytes == baseline && viewport.DisplayCacheBytes == 0, "Closing workspace retained image/cache resources.");
        var after = await HashFiles(examplesRoot); Check(originalHashes.OrderBy(p => p.Key).SequenceEqual(after.OrderBy(p => p.Key)), "A built-in example was overwritten.");
        var summary = new { Passed = true, OldProjects = oldProjects, Headless = headless, Tcp = tcp, Desktop = desktop,
            DashboardDoesNotRunAlgorithms = true, ConfigurationOnlyExamplePreview = true, NavigationDraftsPreserved = true, MonitorUsesExistingRuntime = true,
            Real82MP = true, ExistingRoiSaveReload = true, BuiltInTemplatesUnchanged = true, ConsoleWpfEqual = true,
            ReopenCycles = 5, BuffersBefore = baseline, BuffersAfter = FrameHandle.ActiveBytes, CacheAfterClose = viewport.DisplayCacheBytes,
            PhysicalPrecisionValidated = false, PhysicalSerialHardwareTested = false };
        await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(summary, JsonDefaults.Options));
        Console.WriteLine("PASS: eight real examples, geometry, navigation, old projects, real loopback and resource recovery.");
    }
    private static void CompareOutputs(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> actual, RunResult expected)
    {
        foreach (var (id, outputs) in actual)
            foreach (var (port, value) in outputs)
            {
                var target = expected.Nodes[id].Outputs[port];
                if (value is ImageFrame image && target is ImageFrame other) Check(Hash(image) == Hash(other), "WPF/headless image differs.");
                else if (value is double or int or bool) Check(value.Equals(target), "WPF/headless numeric result differs.");
                else if (value is MeasurementResult measurement && target is MeasurementResult reference)
                    Check(measurement.Valid == reference.Valid && measurement.Values.OrderBy(p => p.Key).SequenceEqual(reference.Values.OrderBy(p => p.Key)), "WPF/headless measurement differs.");
            }
    }
    private static string Hash(ImageFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var lease = frame.Lease();
        for (var y = 0; y < frame.Height; y++) hash.AppendData(lease.GetRowSpan(y)); return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task<Dictionary<string, string>> HashFiles(string directory)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        { await using var stream = File.OpenRead(file); result[Path.GetRelativePath(directory, file)] = Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
        return result;
    }
    private static async Task Idle(Window window) => await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    private static async Task Stable(ImageViewportControl viewport)
    {
        await viewport.Dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await viewport.WaitForDisplayAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    private static async Task Capture(MainWindow window, string folder, string name)
    {
        if (window.Workspace.CurrentPage == WorkspacePage.Editor && window.Session.CurrentOutputs.Count > 0)
            ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1;
        await Idle(window);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }
}

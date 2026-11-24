using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;

internal static partial class Program
{
    private static async Task<int> Beginner(string root)
    {
        var source = Path.Combine(root, "source"); await BeginnerExampleFactory.SaveAllAsync(source);
        var registry = BuiltinRegistration.CreateRegistry(); var store = new ProjectStore(registry); var packages = new ProjectPackageStore(registry);
        var catalog = new ExampleCatalog(source, store); var entries = await catalog.LoadAsync(); var baseline = FrameHandle.ActiveBytes; var rows = new List<object>();
        foreach (var entry in entries)
        {
            Check((await catalog.ValidateAsync(entry)).Count == 0, "Bad manifest " + entry.Manifest.Id);
            var path = await catalog.CopyAsync(entry, Path.Combine(root, "copies")); var project = await store.LoadAsync(path); Dictionary<string, string> hashes;
            using (var run = await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), Path.GetDirectoryName(path)!))
            {
                var verified = ExampleVerifier.Verify(entry.Manifest, run); Check(verified.Passed, verified.Summary); hashes = BufferHashes(run);
                if (entry.Manifest.Id == BeginnerExampleFactory.Ids[4])
                {
                    var items = ((VisionCollection)run.Nodes["each"].Outputs["Results"]).Items.Cast<IterationItem>().ToArray();
                    Check(items.Select(i => i.Result!.Number).SequenceEqual(new double?[] { 1, 2, 3 }), "Loop numbers differ");
                    Check(items.Select(i => i.State).SequenceEqual(new[] { BusinessState.Ng, BusinessState.Ok, BusinessState.Ok }), "Loop business state differs");
                }
            }
            var package = Path.Combine(root, entry.Manifest.Id + ".vspkg"); await packages.ExportAsync(path, package);
            var relocated = await packages.ImportAsync(package, new(Path.Combine(root, "relocated", entry.Manifest.Id)));
            using (var run = await new RuntimeExecutor(registry).ExecuteAsync((await store.LoadAsync(relocated)).ToGraph(), Path.GetDirectoryName(relocated)!))
            { Check(ExampleVerifier.Verify(entry.Manifest, run).Passed, "Relocated lesson failed"); Check(hashes.OrderBy(p => p.Key).SequenceEqual(BufferHashes(run).OrderBy(p => p.Key)), "Crop/preview pixels changed after relocation"); }
            foreach (var host in new[] { false, true })
            {
                var exe = host ? Path.Combine(Path.GetDirectoryName(ConsolePath())!, "VisionPlatform.RuntimeHost.exe") : ConsolePath();
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
                foreach (var arg in host ? new[] { "--project", relocated, "--once" } : new[] { "run", relocated }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
                await File.WriteAllTextAsync(Path.Combine(root, entry.Manifest.Id + (host ? "-host.json" : "-console.log")), await output);
                Check(process.ExitCode == 0, "Published host failed: " + await error);
            }
            Check((await catalog.ValidateAsync(entry)).Count == 0, "Built-in lesson mutated");
            Check(FrameHandle.ActiveBytes == baseline, "Lesson leaked image leases");
            rows.Add(new { Id = entry.Manifest.Id, Runtime = true, ActualConsole = true, ActualRuntimeHost = true, PackageRelocation = true, PixelHashesMatch = true });
        }
        await Report(root, "examples-summary.json", new { Passed = true, Examples = rows, FrameBytesBefore = baseline, FrameBytesAfter = FrameHandle.ActiveBytes, Hardware = false, Python = false, Communication = false });
        return 0;
    }

    private static int BeginnerUi(string root, string source)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new() { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") }); var code = 1;
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null; EditorSession? session = null; var before = FrameHandle.ActiveBytes;
            try
            {
                var config = Path.Combine(root, "machine.json"); Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG", config);
                await new MachineConfigurationStore(config).SaveAsync(new() { SetupCompleted = true, DataDirectory = Path.Combine(root, "data"), CacheDirectory = Path.Combine(root, "cache"), LogDirectory = Path.Combine(root, "logs") });
                session = new EditorSession(); window = new(session, Path.Combine(root, "state")) { Width = 1440, Height = 940, Left = 30, Top = 30 }; window.Show(); await window.WorkspaceReady;
                await window.Examples.ReloadAsync(source); window.Navigate(WorkspacePage.Examples); window.Examples.BeginnerCommand.Execute(null);
                Check(window.Examples.VisibleItems.Count == 5 && session.CurrentOutputs.Count == 0, "Starter navigation must not execute");
                await Capture(window, root, "01-beginner-catalog.png");
                foreach (var id in BeginnerExampleFactory.Ids)
                {
                    window.Examples.Selected = window.Examples.Items.Single(e => e.Manifest.Id == id); await window.Examples.RunSelectedAsync();
                    Check(window.Examples.LastVerification?.Passed == true, "WPF lesson failed: " + window.Examples.Status);
                    session.SelectedNode = session.Nodes.Single(n => n.NodeId == (id.EndsWith("loop") || id.EndsWith("collection") ? "display" : id.EndsWith("display") ? "count" : "cropped"));
                    var tabs = (TabControl)window.FindName("InspectorTabs"); tabs.SelectedIndex = id.EndsWith("loop") || id.EndsWith("collection") ? 2 : 1;
                    window.FitEditorViews(); await Capture(window, root, id + ".png");
                    var saved = Path.Combine(session.ResourceRoot, "lesson-reopened.json"); await session.SaveAsync(saved); await session.NewAsync();
                    Check(session.CurrentOutputs.Count == 0, "New project kept stale results"); await session.OpenAsync(saved); await session.RunAsync();
                    Check(session.Results.All(n => n.Status == "Success"), "Lesson reopen failed");
                }
                window.Navigate(WorkspacePage.Examples); window.Examples.Selected = window.Examples.Items.Single(e => e.Manifest.Id == BeginnerExampleFactory.Ids[4]);
                window.Width = 1024; window.Height = 720; await Capture(window, root, "07-narrow-guide.png");
                await Report(root, "ui-summary.json", new { Passed = true, ShownWindow = true, ProgrammaticInteractions = true, Lessons = 5, NavigationDoesNotExecute = true, SaveReopen = true, Narrow = "1024x720", Hardware = false }); code = 0;
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally
            {
                if (window is not null) { await window.AiPage.PrepareCloseAsync(_ => Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks)); await window.CloseEditorAsync(true); }
                session?.Dispose(); await Report(root, "ui-release.json", new { Before = before, After = FrameHandle.ActiveBytes }); if (before != FrameHandle.ActiveBytes) code = 1; app.Shutdown(code);
            }
        };
        app.Run(); return code;
    }
}

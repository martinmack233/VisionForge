using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Rect = OpenCvSharp.Rect;

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
            catch (Exception ex) { System.Console.Error.WriteLine(ex); }
            finally { app.Shutdown(exit); }
        };
        app.Run(); return exit;
    }
    private static NodeSpecification N(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    private static NodeConnection E(string id, string port, string target, string input) => new(id, port, target, input);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Output<T>(EditorSession session, string id, string port)
    {
        session.SelectedNode = session.Nodes.Single(n => n.NodeId == id);
        return (T)session.SelectedOutputs.Single(o => o.PortName == port).Value;
    }
    private static ProjectDocument BranchInspection() => new()
    {
        ProjectName = "B1-Blob数量真实条件分支",
        Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("threshold", "Threshold", new { ThresholdValue = 128 }),
            N("blob", "BlobDetection", new { }), N("filter", "BlobFilter", new { }), N("compare", "Compare", new { Operator = "GreaterThan", ValueB = 1 }),
            N("if", "IfElse", new { }), N("ng", "LogicNot", new { Value = true }), N("ok", "LogicNot", new { Value = false }),
            N("merge", "Merge", new { }), N("report", "ResultDisplay", new { })],
        Connections = [E("input", "Image", "threshold", "Image"), E("threshold", "ProcessedImage", "blob", "Image"),
            E("blob", "BlobResults", "filter", "BlobResults"), E("filter", "BlobCount", "compare", "ValueA"), E("compare", "Result", "if", "Condition"),
            E("if", "True", "ng", NodePorts.Execute), E("if", "False", "ok", NodePorts.Execute),
            E("ng", NodePorts.Completed, "merge", "TrueControl"), E("ng", "Result", "merge", "TrueValue"),
            E("ok", NodePorts.Completed, "merge", "FalseControl"), E("ok", "Result", "merge", "FalseValue"),
            E("merge", "Boolean", "report", "IsOk"), E("filter", "BlobCount", "report", "BlobCount")]
    };
    private static ProjectDocument SendInspection(int port) => new()
    {
        ProjectName = "B2-真实视觉结果TCP发送",
        Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("blob", "BlobDetection", new { }), N("decision", "ResultDecision", new { }),
            N("report", "ResultDisplay", new { }), N("connect", "TcpConnect", new { Options = new { Port = port } }),
            N("send", "TcpSend", new { SendEnabled = true, DeliveryKey = "sample-inspection-1" }), N("sent", "ResultDisplay", new { })],
        Connections = [E("input", "Image", "blob", "Image"), E("blob", "BlobCount", "decision", "BlobCount"), E("decision", "Decision", "report", "Decision"),
            E("blob", "BlobCount", "report", "BlobCount"), E("report", NodePorts.Completed, "connect", NodePorts.Execute),
            E("connect", "Connection", "send", "Connection"), E("report", "Result", "send", "Result"), E("send", "Status", "sent", "Communication"),
            E("decision", "Decision", "sent", "Decision"), E("blob", "BlobCount", "sent", "BlobCount")]
    };
    private static ProjectDocument ConditionalSend(int port, bool ok) => new()
    {
        ProjectName = ok ? "B3-仅发送OK分支" : "B3-仅发送NG分支",
        Nodes = [N("input", "ImageInput", new { Path = "inspection.png" }), N("blob", "BlobDetection", new { }),
            N("decision", "ResultDecision", new { MinimumBlobCount = ok ? 2 : 0, MaximumBlobCount = ok ? 2 : 0 }),
            N("if", "IfElse", new { }), N("connect", "TcpConnect", new { Options = new { Port = port } }),
            N("ok", "TcpSend", new { SendEnabled = true, DeliveryKey = "conditional-inspection-1", LiteralMessage = "OK" }),
            N("ng", "TcpSend", new { SendEnabled = true, DeliveryKey = "conditional-inspection-1", LiteralMessage = "NG" }),
            N("merge", "Merge", new { }), N("report", "ResultDisplay", new { })],
        Connections = [E("input", "Image", "blob", "Image"), E("blob", "BlobCount", "decision", "BlobCount"), E("decision", "IsOk", "if", "Condition"),
            E("if", NodePorts.Completed, "connect", NodePorts.Execute), E("connect", "Connection", "ok", "Connection"), E("connect", "Connection", "ng", "Connection"),
            E("if", "True", "ok", NodePorts.Execute), E("if", "False", "ng", NodePorts.Execute),
            E("ok", NodePorts.Completed, "merge", "TrueControl"), E("ok", "Status", "merge", "TrueValue"),
            E("ng", NodePorts.Completed, "merge", "FalseControl"), E("ng", "Status", "merge", "FalseValue"),
            E("merge", "Communication", "report", "Communication"), E("decision", "Decision", "report", "Decision"), E("blob", "BlobCount", "report", "BlobCount")]
    };

    private static async Task Verify(string root)
    {
        var samples = Path.Combine(root, "samples", "phase3b"); Directory.CreateDirectory(samples);
        var evidence = Path.Combine(root, "artifacts", "phase3b-acceptance"); Directory.CreateDirectory(evidence);
        using (var image = new Mat(120, 160, MatType.CV_8UC1, Scalar.All(20)))
        {
            Cv2.Rectangle(image, new Rect(20, 20, 20, 16), Scalar.All(230), -1);
            Cv2.Rectangle(image, new Rect(70, 30, 14, 12), Scalar.All(230), -1);
            Cv2.ImWrite(Path.Combine(samples, "inspection.png"), image);
        }
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry());
        var first = BranchInspection(); var path = Path.Combine(samples, "flow1.json"); await store.SaveAsync(path, first);
        var before = FrameHandle.ActiveBytes;
        var session = new EditorSession(); var window = new MainWindow(session); window.Show();
        var viewport = (ImageViewportControl)window.FindName("Viewport");
        await session.OpenAsync(path); await session.RunAsync();
        Check(session.Results.Single(n => n.NodeId == "ok").Status == "Skipped" && session.Results.Single(n => n.NodeId == "ng").Status == "Success", "True branch was not exclusive.");
        var report = Output<InspectionReport>(session, "report", "Result"); Check(!report.IsOk && report.BlobCount == 2, "Measured Blob/branch output is wrong.");
        await Capture(window, viewport, evidence, "flow1-true.png");
        var compare = session.Nodes.Single(n => n.NodeId == "compare"); session.CommitParameters(compare, JsonDefaults.Element(new { Operator = "GreaterThan", ValueB = 5 }));
        Check(session.Results.Count == 0 && session.CurrentInspectionReport is null, "Editing retained stale branch display.");
        await session.RunAsync(); report = Output<InspectionReport>(session, "report", "Result");
        Check(report.IsOk && session.Results.Single(n => n.NodeId == "ng").Status == "Skipped", "False branch reused a stale True value.");
        var saved = Path.Combine(samples, "flow1-reopened.json"); await session.SaveAsync(saved); await session.NewAsync(); await session.OpenAsync(saved); await session.RunAsync();
        Check(Output<InspectionReport>(session, "report", "Result").IsOk, "Saved control graph did not reopen.");
        await Capture(window, viewport, evidence, "flow1-false.png");
        await ConsoleVerify(root, saved, evidence);
        var flowRows = new List<object>();
        foreach (var scenario in new[] { 2, 3, 4 })
        {
            var peerFile = Path.Combine(evidence, $"tcp{scenario}.json");
            using var peer = StartPeer(root, peerFile);
            var ready = await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var readyJson = JsonDocument.Parse(ready ?? throw new IOException("TCP peer did not start."));
            var port = readyJson.RootElement.GetProperty("port").GetInt32();
            var project = scenario == 2 ? SendInspection(port) : ConditionalSend(port, scenario == 3);
            var projectPath = Path.Combine(samples, scenario == 2 ? "flow2.json" : scenario == 3 ? "flow3-ok.json" : "flow3-ng.json");
            await store.SaveAsync(projectPath, project); await session.OpenAsync(projectPath); await session.RunAsync();
            var chosen = scenario == 2 ? "send" : scenario == 3 ? "ok" : "ng";
            var state = Output<CommunicationStatus>(session, chosen, "Status"); Check(state.State == CommunicationState.Sent, "First explicit send was not executed.");
            if (scenario != 2) Check(session.Results.Single(n => n.NodeId == (scenario == 3 ? "ng" : "ok")).Status == "Skipped", "Unselected communication branch executed.");
            await session.RunAsync(); state = Output<CommunicationStatus>(session, chosen, "Status");
            Check(state.State == CommunicationState.DuplicateSuppressed, "Repeated run resent the same delivery key.");
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == (scenario == 2 ? "sent" : "report"));
            Check(session.CurrentInspectionReport?.Communication?.State == CommunicationState.DuplicateSuppressed, "Structured communication status binding is stale.");
            await Capture(window, viewport, evidence, $"tcp{scenario}.png");
            await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await peer.StandardOutput.ReadToEndAsync(); var errors = await peer.StandardError.ReadToEndAsync();
            File.WriteAllText(Path.Combine(evidence, $"peer{scenario}.log"), ready + "\n" + output + errors);
            Check(peer.ExitCode == 0, "TCP peer found extra or malformed messages: " + errors);
            using var received = JsonDocument.Parse(File.ReadAllText(peerFile));
            var messages = received.RootElement.GetProperty("messages"); Check(messages.GetArrayLength() == 1, "Repeated/unselected branch sent data.");
            var message = messages[0].GetString()!;
            if (scenario == 2)
            {
                using var wire = JsonDocument.Parse(message); Check(wire.RootElement.GetProperty("result").GetString() == "OK" && wire.RootElement.GetProperty("count").GetInt32() == 2, "Wire result differs from actual inspection.");
            }
            else Check(message == (scenario == 3 ? "OK" : "NG"), "Wrong branch reached the real peer.");
            flowRows.Add(new { Scenario = scenario, Passed = true, Port = port, Received = message, RepeatedRun = state.State, Nodes = session.Results.ToArray() });
        }

        var free = new TcpListener(IPAddress.Loopback, 0); free.Start(); var unavailable = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        var badPath = Path.Combine(samples, "flow4-connection-error.json");
        await store.SaveAsync(badPath, new ProjectDocument { ProjectName = "B4-受控连接失败", Nodes = [N("connect", "TcpConnect", new { Options = new { Port = unavailable, ConnectTimeoutMilliseconds = 150 } })] });
        await session.OpenAsync(badPath); await session.RunAsync();
        Check(session.Results.Single().Status == "Failed" && !string.IsNullOrWhiteSpace(session.Results.Single().Error), "Connection failure was not visible.");
        await Capture(window, viewport, evidence, "connection-error.png");
        // Every previous A project still opens and executes with the same runtime behavior.
        foreach (var name in new[] { "flow1.json", "flow2.json", "flow3.json" })
        {
            await session.OpenAsync(Path.Combine(root, "samples", "phase3a", name)); await session.RunAsync();
            Check(session.Results.All(r => r.Status == "Success"), "Phase A project compatibility failed: " + name);
        }
        await window.CloseEditorAsync(true); Check(FrameHandle.ActiveBytes == before, "Closing retained image buffers.");
        // Deliver manual examples on one documented loopback port. Test evidence records the
        // independently assigned ephemeral endpoints used above.
        await store.SaveAsync(Path.Combine(samples, "flow2.json"), SendInspection(9000));
        await store.SaveAsync(Path.Combine(samples, "flow3-ok.json"), ConditionalSend(9000, true));
        await store.SaveAsync(Path.Combine(samples, "flow3-ng.json"), ConditionalSend(9000, false));
        var summary = new { Passed = true, SyntheticImages = true, PhysicalSerialTested = false, IfElseTrueAndFalse = true, MergeCurrentOnly = true,
            SavedReopened = true, ConsoleMatchesWpf = true, PhaseAProjectsOpened = true, Tcp = flowRows, ConnectionErrorReported = true,
            BuffersBefore = before, BuffersAfter = FrameHandle.ActiveBytes };
        File.WriteAllText(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(summary, JsonDefaults.Options));
        System.Console.WriteLine(JsonSerializer.Serialize(summary, JsonDefaults.Options));
    }

    private static Process StartPeer(string root, string output)
    {
        var info = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-u", Path.Combine(root, "tools", "tcp_fixture.py"), "--port", "0", "--output", output, "--linger", "0.8" }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("TCP fixture did not start.");
    }
    private static async Task ConsoleVerify(string root, string project, string evidence)
    {
        var directory = Path.Combine(evidence, "console");
        var exe = Path.Combine(root, "src/VisionPlatform.Console/bin/Release/net10.0/VisionPlatform.Console.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", project, "--outputs", directory }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "console.log"), await stdout + await stderr);
        Check(process.ExitCode == 0, "Console failed to execute control graph.");
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results.json")));
        var nodes = result.RootElement.GetProperty("Nodes");
        Check(nodes.GetProperty("ng").GetProperty("Status").GetString() == "Skipped" &&
            nodes.GetProperty("report").GetProperty("Outputs").GetProperty("Result").GetProperty("IsOk").GetBoolean(), "Console/WPF control output differs.");
    }
    private static async Task Capture(MainWindow window, ImageViewportControl viewport, string evidence, string name)
    {
        window.FitEditorViews(); await viewport.WaitForPreviewAsync(); ((TabControl)window.FindName("InspectorTabs")).SelectedIndex = 1;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(evidence, name)); encoder.Save(file);
    }
}

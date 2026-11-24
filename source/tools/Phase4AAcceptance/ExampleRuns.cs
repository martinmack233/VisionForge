using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Imaging;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;

internal static class ExampleRuns
{
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static ProjectStore Store(NodeRegistry registry) => new(registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
    internal static async Task<IReadOnlyList<object>> Headless(ExampleCatalog catalog, IReadOnlyList<ExampleEntry> entries, NodeRegistry registry)
    {
        var rows = new List<object>();
        foreach (var entry in entries)
        {
            var errors = await catalog.ValidateAsync(entry); Check(errors.Count == 0, string.Join(";", errors));
            var project = await Store(registry).LoadAsync(entry.ProjectPath);
            using (var run = await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), entry.Directory))
            {
                var verification = ExampleVerifier.Verify(entry.Manifest, run); Check(verification.Passed, entry.Manifest.Id + ": " + verification.Summary);
                rows.Add(Row(entry.Manifest.Id, "default", run));
            }
            foreach (var variant in entry.Manifest.Cases)
            {
                var nodes = project.Nodes.Select(n => n.NodeTypeId == "ImageInput" ? n with { Parameters = JsonDefaults.Element(
                    n.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)! with { Path = variant.ImagePath }) } : n).ToList();
                using var run = await new RuntimeExecutor(registry).ExecuteAsync(new NodeGraph(nodes, project.Connections), entry.Directory);
                var verification = ExampleVerifier.Verify(entry.Manifest, run, variant.Id);
                Check(verification.Passed, entry.Manifest.Id + "/" + variant.Id + ": " + verification.Summary);
                rows.Add(Row(entry.Manifest.Id, variant.Id, run));
            }
        }
        return rows;
    }
    private static object Row(string example, string variant, RunResult run) => new
    {
        Example = example, Variant = variant, run.Status, Milliseconds = run.ExecutionTime.TotalMilliseconds,
        Nodes = run.Nodes.Values.Select(n => new { n.NodeId, n.Status, Milliseconds = n.ExecutionTime.TotalMilliseconds, n.ErrorMessage }).ToArray()
    };
    internal static async Task<object> Tcp(ExampleEntry entry, NodeRegistry registry)
    {
        var project = await Store(registry).LoadAsync(entry.ProjectPath); RunResult? run = null;
        try
        {
            var received = await LocalTcpExample.RunAsync(async (port, ct) =>
            {
                var nodes = project.Nodes.Select(n => n.NodeId switch
                {
                    "enable" => n with { Parameters = JsonDefaults.Element(new { Value = false }) },
                    "connect" => n with { Parameters = JsonDefaults.Element(n.Parameters.Deserialize<TcpConnectParameters>(JsonDefaults.Options)! with
                        { Options = n.Parameters.Deserialize<TcpConnectParameters>(JsonDefaults.Options)!.Options with { Port = port } }) },
                    "send" => n with { Parameters = JsonDefaults.Element(n.Parameters.Deserialize<TcpSendParameters>(JsonDefaults.Options)! with
                        { SendEnabled = true, DeliveryKey = "phase4a-" + Guid.NewGuid().ToString("N") }) },
                    _ => n
                }).ToList();
                run = await new RuntimeExecutor(registry).ExecuteAsync(new NodeGraph(nodes, project.Connections), entry.Directory, ct);
                Check(ExampleVerifier.Verify(entry.Manifest, run, communication: true).Passed, "Real loopback graph failed.");
            });
            Check(received == run!.Nodes["report"].Output<string>("Message"), "Loopback bytes differ from the real structured detection message.");
            return new { Passed = true, ActualSocketBytes = received, ServicesReleased = true };
        }
        finally { run?.Dispose(); }
    }
    internal static async Task ConsoleRun(string root, string project, string output)
    {
        var info = new ProcessStartInfo(Path.Combine(root, "src/VisionPlatform.Console/bin/Release/net10.0/VisionPlatform.Console.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "run", project, "--outputs", output }) info.ArgumentList.Add(value);
        using var child = Process.Start(info)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "console.log"), await stdout + await stderr);
        Check(child.ExitCode == 0, "Independent Console failed: " + project);
        var registry = BuiltinRegistration.CreateRegistry(); var document = await Store(registry).LoadAsync(project);
        using var reference = await new RuntimeExecutor(registry).ExecuteAsync(document.ToGraph(), Path.GetDirectoryName(project)!);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "results.json")));
        var images = 0; var measurements = 0;
        foreach (var node in reference.Nodes.Values)
            foreach (var (name, value) in node.Outputs)
            {
                var actual = json.RootElement.GetProperty("Nodes").GetProperty(node.NodeId).GetProperty("Outputs").GetProperty(name);
                if (value is ImageFrame image)
                {
                    using var pixels = ImageFile.Load(Path.Combine(output, actual.GetProperty("File").GetString()!), image.PixelFormat);
                    Check(Hash(pixels) == Hash(image), "Console exported different pixels."); images++;
                }
                else if (value is MeasurementResult measurement)
                {
                    var decoded = actual.Deserialize<MeasurementResult>(JsonDefaults.Options)!;
                    Check(decoded.Valid == measurement.Valid && decoded.Values.OrderBy(p => p.Key).SequenceEqual(measurement.Values.OrderBy(p => p.Key)), "Console measurement values differ."); measurements++;
                }
                else if (value is double or int or bool)
                    Check(JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()), JsonSerializer.SerializeToNode(value, JsonDefaults.Options)), "Console numeric result differs.");
            }
        Check(images > 0, "No independent Console image was compared.");
        File.WriteAllText(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new { Passed = true, Images = images, Measurements = measurements }, JsonDefaults.Options));
    }
    private static string Hash(ImageFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var lease = frame.Lease();
        for (var y = 0; y < frame.Height; y++) hash.AppendData(lease.GetRowSpan(y)); return Convert.ToHexString(hash.GetHashAndReset());
    }
}

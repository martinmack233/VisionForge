using VisionPlatform.Contracts.Collections;
using System.Text.Json;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;

using var cancellation = new CancellationTokenSource();
System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try { return await MainAsync(args, cancellation.Token); }
catch (Exception ex) { System.Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }

static async Task<int> MainAsync(string[] args, CancellationToken ct)
{
    if (Option(args, "--machine-config") is { } machinePath) Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG", Path.GetFullPath(machinePath));
    if (args.Contains("--version")) { System.Console.WriteLine($"{VisionPlatform.Contracts.Build.ProductBuild.Name} {VisionPlatform.Contracts.Build.ProductBuild.Version}\n{VisionPlatform.Contracts.Build.ProductBuild.Runtime}\n{VisionPlatform.Contracts.Build.ProductBuild.Identity}"); return 0; }
    var registry = BuiltinRegistration.CreateRegistry();
    var executor = new RuntimeExecutor(registry);
    var matcher = new CvInspectTemplateMatcher();
    var store = new ProjectStore(registry, new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version });
    var command = args.FirstOrDefault() ?? "demo";
    if(command=="beginner-examples"){await BeginnerExampleFactory.SaveAllAsync(Path.GetFullPath(args.ElementAtOrDefault(1)??"samples/Beginners"),ct);return 0;}
    if(command=="operator-examples"){var root=Path.GetFullPath(args.ElementAtOrDefault(1)??"samples/Operators");await OperatorExpansionSamples.SaveAllAsync(root,ct);await OperatorCommunicationDemo.SaveAllAsync(root,ct);return 0;}
    if(command=="operator-rtu-sim"){System.Console.WriteLine(JsonSerializer.Serialize(await OperatorCommunicationDemo.RunRtuSimulationAsync(ct),JsonDefaults.Options));return 0;}
    if (command == "inspection-examples") { await InspectionExampleFactory.SaveAllAsync(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "samples/Inspections"), ct); return 0; }
    if (command == "collection-examples") { await CollectionExampleFactory.SaveAllAsync(Path.GetFullPath(args.ElementAtOrDefault(1) ?? "samples/Collections"), ct); return 0; }
    if (command == "diagnostics")
    {
        System.Console.WriteLine(OpenCvSharp.Cv2.GetBuildInformation());
        return 0;
    }
    if (command == "demo")
    {
        var directory = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "samples/DemoInspection");
        DemoProjectFactory.CreateResources(directory);
        foreach (var preprocess in new[] { false, true })
        {
            var file = Path.Combine(directory, preprocess ? "flow-b.json" : "flow-a.json");
            await store.SaveAsync(file, DemoProjectFactory.Create(preprocess), ct);
            var project = await store.LoadAsync(file, ct);
            using var result = await executor.ExecuteAsync(project.ToGraph(), directory, ct);
            Print(project.ProjectName, result);
            Export(result, Path.Combine(directory, preprocess ? "outputs-b" : "outputs-a"));
            if (!result.Success || !result.Nodes["decision"].Output<bool>("IsOk")) return 2;
        }
        System.Console.WriteLine("Saved projects: " + directory);
        return 0;
    }
    if (command.StartsWith("package-", StringComparison.Ordinal))
    {
        var packages = new ProjectPackageStore(registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
        if (command == "package-export" && args.Length >= 3)
        {
            var manifest = await packages.ExportAsync(args[1], args[2], new(args.Contains("--include-models"), args.Contains("--confirm-asset-rights")), ct);
            System.Console.WriteLine(JsonSerializer.Serialize(manifest, JsonDefaults.Options)); return 0;
        }
        if (command == "package-inspect" && args.Length >= 2)
        {
            var inspection = await packages.InspectAsync(args[1], ct); System.Console.WriteLine(JsonSerializer.Serialize(inspection, JsonDefaults.Options)); return inspection.CanImport ? 0 : 2;
        }
        if (command == "package-import" && args.Length >= 3)
        {
            Dictionary<string, string>? relocations = null;
            if (Option(args, "--relocations") is { } mapping)
            {
                if (new FileInfo(mapping).Length > 1_000_000) throw new ArgumentException("Resource relocation JSON exceeds 1 MiB.");
                relocations = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(mapping, ct));
            }
            var imported = await packages.ImportAsync(args[1], new(args[2], Option(args, "--name"), relocations), ct); System.Console.WriteLine(imported); return 0;
        }
        throw new ArgumentException("package-export project.json new.vspkg [--include-models --confirm-asset-rights]; package-inspect file.vspkg; package-import file.vspkg NEW-directory [--name name] [--relocations mapping.json]");
    }
    if (command == "run" && args.Length >= 2)
    {
        var path = Path.GetFullPath(args[1]);
        var project = await store.LoadAsync(path, ct);
        var overrideImage = Option(args, "--image");
        if (overrideImage is not null)
            project = project with { Nodes = project.Nodes.Select(n => n.NodeTypeId == "ImageInput"
                ? n with { Parameters = JsonDefaults.Element(new ImageInputParameters { Path = Path.GetFullPath(overrideImage) }) } : n).ToList() };
        var root = Path.GetDirectoryName(path)!;
        var repeat = int.Parse(Option(args,"--repeat") ?? "1",System.Globalization.CultureInfo.InvariantCulture);
        if(repeat is <1 or >10000)throw new ArgumentException("--repeat must be 1..10000; explicit runs may execute configured side effects.");
        await using var runtimeSession=new RuntimeExecutionSession();var repeating=new RuntimeExecutor(registry,null,runtimeSession);
        for(var iteration=0;iteration<repeat;iteration++)
        {
        ct.ThrowIfCancellationRequested();
        using var run = Option(args, "--to") is { } target
            ? await repeating.ExecuteToAsync(project.ToGraph(), target, root, ct)
            : await repeating.ExecuteAsync(project.ToGraph(), root, ct);
        Print(project.ProjectName, run);
        if (Option(args, "--outputs") is { } outputs) Export(run, Path.GetFullPath(outputs));
        if (!run.Success) return 2;
        if(iteration==repeat-1)return InspectionSucceeded(run)?0:2;
        }
        return 0;
    }
    if (command == "match" && args.Length >= 3)
    {
        using var image = ImageFile.Load(args[1]);
        using var template = TemplateResourceStore.Load(args[2]);
        var spec = DemoProjectFactory.Node("match", "TemplateMatching", new TemplateMatchingParameters());
        using var run = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image, ["Template"] = template }, ".", ct);
        Print("Single template matching", run);
        return run.Success && run.Nodes["match"].Output<bool>("Success") ? 0 : 2;
    }
    if (command == "blur" && args.Length >= 3)
    {
        using var image = ImageFile.Load(args[1]);
        var kernel = int.Parse(args.ElementAtOrDefault(3) ?? "5", System.Globalization.CultureInfo.InvariantCulture);
        var sigma = double.Parse(args.ElementAtOrDefault(4) ?? "1.2", System.Globalization.CultureInfo.InvariantCulture);
        var spec = DemoProjectFactory.Node("blur", "GaussianBlur", new GaussianBlurParameters { Options = new GaussianBlurOptions { KernelSize = kernel, SigmaX = sigma } });
        using var run = await executor.ExecuteSingleAsync(spec, new Dictionary<string, object> { ["Image"] = image }, ".", ct);
        Print("Single Gaussian blur", run);
        if (!run.Success) return 2;
        ImageFile.Save(run.Nodes["blur"].Output<ImageFrame>("ProcessedImage"), args[2]);
        return 0;
    }
    System.Console.WriteLine($"{VisionPlatform.Contracts.Build.ProductBuild.Name} Console (.NET 10 / win-x64)\n" +
        "  demo [directory]\n  run project.json [--image image.png] [--to nodeId] [--outputs directory] [--repeat 1..10000]\n  beginner-examples NEW-directory\n  inspection-examples NEW-directory\n" +
        "  match image.png template.json\n  blur input.png output.png [oddKernel] [sigma]\n  diagnostics\n" +
        "  package-export project.json NEW.vspkg [--include-models --confirm-asset-rights]\n  package-inspect file.vspkg\n  package-import file.vspkg NEW-directory [--name name] [--relocations mapping.json]\n" +
        "Machine environment: --machine-config absolute.json or VISIONSTUDIO_MACHINE_CONFIG (shared by WPF/Console/Host).");
    return command is "help" or "--help" ? 0 : 1;
}

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 ? args.ElementAtOrDefault(index + 1) ?? throw new ArgumentException("Missing value for " + name) : null;
}
static void Print(string project, RunResult run)
{
    System.Console.WriteLine($"\n{VisionPlatform.Contracts.Build.ProductBuild.Name} Console\nProject: {project}\n--------------------------------");
    foreach (var (id, node) in run.Nodes)
    {
        System.Console.WriteLine($"[{id}] Status: {node.Status}" + (node.SkipReason == NodeSkipReason.None ? "" : $" ({node.SkipReason})") + $"; {node.ExecutionTime.TotalMilliseconds:F3} ms");
        foreach (var (port, value) in node.Outputs)
        {
            var text = value switch
            {
                VisionPlatform.Contracts.Images.FloatPlaneFrame or VisionPlatform.Contracts.Numerics.NumericMatrix or VisionPlatform.Contracts.Geometry.ProjectiveTransform2D or VisionPlatform.Contracts.Data.BinaryData or VisionPlatform.Contracts.Data.FileReadResult or VisionPlatform.Communication.Modbus.ModbusResult or VisionPlatform.Communication.Modbus.ModbusRegisterValues or VisionPlatform.Communication.Modbus.ModbusBitValues=>JsonSerializer.Serialize(VisionPlatform.Hosting.HostResultProjector.ProjectValue(value),JsonDefaults.Options),
                VisionCollection c => $"{c.Kind} · {c.Count} items; " + string.Join(" | ", c.Items.Take(20).Select(i => $"{i.Origin.SourceIndex}:{i.State}:" + string.Join(",", i.Fields.Select(f => f.Key + "=" + f.Value.Format(6))))),
                CollectionDisplayResult display => display.Summary,
                IterationBatch batch => $"Complete={batch.Complete}, Cancelled={batch.Cancelled}, Completed={batch.CompletedCount}/{batch.Results.Count}; {batch.Error}",
                VisionPlatform.Runtime.Execution.IterationTraceStore trace => $"{trace.Traces.Count} retained debug iterations; {trace.RetainedBytes} image bytes",
                ImageFrame image => $"{image.Width} x {image.Height} {image.PixelFormat}; origin=({image.CoordinateSpace.OriginX},{image.CoordinateSpace.OriginY})",
                TemplateMatchResult m => $"Success={m.Success}, Score={m.Score:F6}, Center=({m.CenterX:F3},{m.CenterY:F3}), Angle={m.AngleDegrees:F3}, Scale={m.Scale:F4}" + (m.FailureReason is null ? "" : "; " + m.FailureReason),
                InspectionDecision d => (d.IsOk ? "OK" : "NG") + "; " + d.Reason,
                BlobResults b => $"{b.Count} objects",
                _ => JsonSerializer.Serialize(value, value.GetType(), JsonDefaults.Options)
            };
            System.Console.WriteLine($"  {port}: {text}");
        }
        if (node.ErrorMessage is { } error) System.Console.WriteLine("  Error: " + error);
    }
    var hasDecision=run.Nodes.Values.SelectMany(n=>n.Outputs.Values).Any(v=>v is InspectionReport or InspectionDecision)||run.Nodes.TryGetValue("decision",out var finalDecision)&&finalDecision.Outputs.ContainsKey("IsOk");
    var business=!run.Success?"执行异常/取消":hasDecision?(InspectionSucceeded(run)?"OK":"NG"):"未配置判定";
    System.Console.WriteLine($"Execution: {run.Status}; Result: {business}; Total: {run.ExecutionTime.TotalMilliseconds:F3} ms");
}
static bool InspectionSucceeded(RunResult run)
{
    if (!run.Success) return false;
    var reports = run.Nodes.Values.SelectMany(n => n.Outputs.Values).OfType<InspectionReport>().ToArray();
    if(reports.Length>0)return reports.All(r=>r.IsOk);var decisions=run.Nodes.Values.SelectMany(n=>n.Outputs.Values).OfType<InspectionDecision>().ToArray();
    return decisions.Length>0?decisions.All(d=>d.IsOk):!run.Nodes.TryGetValue("decision",out var decision)||decision.Output<bool>("IsOk");
}
static void Export(RunResult run, string directory)
{
    Directory.CreateDirectory(directory);
    var nodes = new Dictionary<string, object>();
    foreach (var (id, node) in run.Nodes)
    {
        var outputs = new Dictionary<string, object>();
        foreach (var (name, value) in node.Outputs)
        {
            if (value is ImageFrame image)
            {
                var filename = $"{SafeName(id)}.{SafeName(name)}.png";
                ImageFile.Save(image, Path.Combine(directory, filename));
                outputs[name] = new { image.Width, image.Height, image.PixelFormat, image.CoordinateSpace, File = filename };
            }
            else outputs[name] = value;
        }
        nodes[id] = new { node.Status, node.SkipReason, node.ExecutionTime, node.ErrorMessage, Outputs = outputs };
    }
    File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { run.Status, run.ExecutionTime, Nodes = nodes }, JsonDefaults.Options));
}
static string SafeName(string value)
{
    var readable = new string(value.Take(60).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..8];
    return readable + "-" + hash;
}

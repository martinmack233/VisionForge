using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;

internal static partial class Program
{
    [STAThread] private static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task<int> Run(string[] args)
    {
        var command = args[0]; var root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        if (command == "bench") { await Bench(root, args[2], args[3], args[4], args[5], args.Length > 6 ? int.Parse(args[6]) : 25); return 0; }
        return await Other(command, root, args);
    }
    private static async Task Bench(string root, string component, string python, string model, string image, int count)
    {
        var verified = await YoloDeploymentVerifier.VerifyAsync(model); string? canonical = null; var modes = new List<object>();
        foreach (var (mode, channel) in new[] { (YoloExecutionMode.PerRequest, YoloImageChannel.Png), (YoloExecutionMode.Resident, YoloImageChannel.Png), (YoloExecutionMode.Resident, YoloImageChannel.UncompressedTiff) })
        {
            await using var session = new RuntimeExecutionSession(); var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry(), null, session);
            var graph = new NodeGraph([DemoProjectFactory.Node("image", "ImageInput", new ImageInputParameters { Path = Path.GetFullPath(image) }),
                DemoProjectFactory.Node("yolo", "YoloInference", new YoloInferenceOptions { Mode = mode, ImageChannel = channel, PythonExecutable = Path.GetFullPath(python), YoloStudioRoot = Path.GetFullPath(component), ModelPath = verified.ModelPath, ImageSize = verified.InputSize, ExpectedModelSha256 = verified.ModelSha256, ExpectedDeploymentSha256 = verified.DeploymentSha256 }),
                DemoProjectFactory.Node("adapter", "CollectionAdapter", new CollectionAdapterOptions()), DemoProjectFactory.Node("each", "ForEach", new VisionPlatform.Contracts.Collections.ForEachOptions { Body = CollectionSubflows.Measure() })],
                [new("image","Image","yolo","Image"),new("yolo","Result","adapter","Detections"),new("adapter","Collection","each","Collection")]);
            var rows = new List<object>(); var times = new List<double>(); Guid previous = Guid.Empty;
            var n = mode == YoloExecutionMode.PerRequest ? Math.Min(count, 3) : count;
            for (var i = 0; i < n; i++)
            {
                using var run = await executor.ExecuteAsync(graph, root);
                if (!run.Success) throw new Exception(string.Join(";", run.Nodes.Values.Select(v => v.ErrorMessage)));
                var result = run.Nodes["yolo"].Output<YoloInferenceResult>("Result");
                var source = run.Nodes["image"].Output<ImageFrame>("Image");
                if (source.FrameId != result.FrameId || source.FrameId == previous || result.RunId != run.RunId) throw new Exception("Stale frame/run identity."); previous = source.FrameId;
                var boxes = JsonSerializer.Serialize(result.Detections); canonical ??= boxes; if (canonical != boxes) throw new Exception("Legacy/resident/channel detection mismatch.");
                var elapsed = run.ExecutionTime.TotalMilliseconds; times.Add(elapsed);
                var process = Process.GetCurrentProcess(); process.Refresh();
                object? workerStats = null;
                if(session.TryGet<ResidentYoloBridge>(ResidentYoloBridge.ServiceKey,out var svc)&&svc!.ProcessId is{} pid){using var worker=Process.GetProcessById(pid);worker.Refresh();workerStats=new{Pid=pid,WorkingSet=worker.WorkingSet64,Handles=worker.HandleCount};}
                rows.Add(new { Worker=workerStats,Index=i, Cold=i==0, FlowMilliseconds=elapsed, result.FrameId, result.RunId, Count=result.Detections.Count, result.Timings, WorkingSet=process.WorkingSet64, Handles=process.HandleCount });
            }
            JsonElement? health = null;
            if (session.TryGet<ResidentYoloBridge>(ResidentYoloBridge.ServiceKey, out var resident))
            { health = await resident!.HealthAsync(); if (health.Value.GetProperty("model_loads").GetInt32()!=1 || health.Value.GetProperty("cached_models").GetInt32()!=1) throw new Exception("Model cache not reused/bounded."); await resident.UnloadAsync(); var unloaded=await resident.HealthAsync(); if(unloaded.GetProperty("cached_models").GetInt32()!=0)throw new Exception("Unload failed."); }
            var hot = times.Skip(1).Order().ToArray(); double Q(double q) => hot.Length == 0 ? times[0] : hot[(int)Math.Ceiling(q * hot.Length)-1];
            modes.Add(new { Mode=mode.ToString(), Channel=channel.ToString(), Runs=n, ColdMilliseconds=times[0], P50=Q(.5), P95=n>=20?Q(.95):(double?)null,P99=n>=100?Q(.99):(double?)null, Health=health, Rows=rows });
        }
        if (FrameHandle.ActiveBytes != 0) throw new Exception("Frame resources survived run disposal.");
        await File.WriteAllTextAsync(Path.Combine(root,"3a-benchmark.json"), JsonSerializer.Serialize(new { VisionPlatform.Contracts.Build.ProductBuild.SourceHash,VisionPlatform.Contracts.Build.ProductBuild.Version,Passed=true, ModelHash=verified.ModelSha256, SameImage=Path.GetFileName(image), SameParameters=true, Modes=modes, ActiveFrameBytes=FrameHandle.ActiveBytes, TrainingStarted=false, GpuUsed=false, NotAnAllDayTest=true },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine("3A legacy/resident PNG/resident TIFF comparison passed.");
    }
    private static partial Task<int> Other(string command, string root, string[] args);
}

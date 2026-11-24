using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Contracts.Build;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Hosting;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;

internal static partial class Program
{
    [STAThread] static int Main(string[] args)
    {try{if(args.Length<2)throw new ArgumentException("examples OUT | ui OUT [EXAMPLESROOT] | beginner OUT | beginner-ui OUT EXAMPLESROOT");var root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);return args[0] switch{"examples"=>Examples(root).GetAwaiter().GetResult(),"ui"=>Ui(root,args.ElementAtOrDefault(2)),"communications"=>Communications(root).GetAwaiter().GetResult(),"beginner"=>Beginner(root).GetAwaiter().GetResult(),"beginner-ui"=>BeginnerUi(root,Path.GetFullPath(args[2])),_=>throw new ArgumentException("Unknown acceptance mode")};}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static Task Report(string root,string file,object value)
    {var node=JsonSerializer.SerializeToNode(value,JsonDefaults.Options)!.AsObject();node["ProductVersion"]=ProductBuild.Version;node["BuildSourceHash"]=ProductBuild.SourceHash;node["SourceState"]=ProductBuild.SourceState;return File.WriteAllTextAsync(Path.Combine(root,file),node.ToJsonString(JsonDefaults.Options));}
    private static JsonNode? Stable(JsonNode? value)
    {
        if(value is JsonObject obj){var result=new JsonObject();foreach(var p in obj.OrderBy(p=>p.Key,StringComparer.Ordinal))if(p.Key is not("FrameId" or "RunId" or "Key" or "ParentKey" or "InspectionId" or "inspectionId" or "Timestamp" or "Milliseconds" or "ExecutionMilliseconds" or "Path"))result[p.Key]=Stable(p.Value);return result;}
        if(value is JsonArray array)return new JsonArray(array.Select(Stable).ToArray());
        // ResultDisplay's JSON message contains a fresh execution inspectionId. Normalize that
        // declared identity while retaining every business field, not arbitrary user text.
        if(value is JsonValue scalar&&scalar.TryGetValue<string>(out var text)&&text.StartsWith('{')){try{var parsed=JsonNode.Parse(text);if(parsed is JsonObject message&&message.ContainsKey("inspectionId"))return Stable(message);}catch(JsonException){}}
        return value?.DeepClone();
    }
    private static string Snapshot(RunResult run)=>Stable(JsonSerializer.SerializeToNode(run.Nodes.OrderBy(p=>p.Key,StringComparer.Ordinal).ToDictionary(p=>p.Key,p=>p.Value.Outputs.OrderBy(o=>o.Key,StringComparer.Ordinal).ToDictionary(o=>o.Key,o=>HostResultProjector.ProjectValue(o.Value).Value)),JsonDefaults.Options))!.ToJsonString();
    private static string HostSnapshot(HostResultSnapshot run)=>Stable(JsonSerializer.SerializeToNode(run.Nodes.OrderBy(n=>n.NodeId,StringComparer.Ordinal).ToDictionary(n=>n.NodeId,n=>n.Outputs.OrderBy(o=>o.Key,StringComparer.Ordinal).ToDictionary(o=>o.Key,o=>o.Value.Value)),JsonDefaults.Options))!.ToJsonString();
    private static Dictionary<string,string> BufferHashes(RunResult run)
    {
        var values=new Dictionary<string,string>();foreach(var node in run.Nodes)foreach(var p in node.Value.Outputs)
        {
            BufferLease? lease=p.Value switch{ImageFrame f=>f.Lease(),FloatPlaneFrame f=>f.Lease(),_=>null};if(lease is null)continue;using(lease){using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);for(var y=0;y<lease.Height;y++)hash.AppendData(lease.GetRowSpan(y));values[node.Key+"."+p.Key]=Convert.ToHexString(hash.GetHashAndReset());}
        }
        return values;
    }
    private static void Verify(RunResult run,ExampleManifest manifest)
    {
        Check(run.Success,run.ErrorMessage??"Runtime failed");foreach(var check in manifest.Expected){Check(run.Nodes.TryGetValue(check.NodeId,out var node)&&node.Outputs.ContainsKey(check.Port),"Expected output missing: "+check.NodeId+"."+check.Port);var actual=JsonSerializer.SerializeToElement(run.Nodes[check.NodeId].Outputs[check.Port],JsonDefaults.Options);foreach(var part in check.Property.Split('.',StringSplitOptions.RemoveEmptyEntries))actual=actual.GetProperty(part);if(check.Expected.ValueKind==JsonValueKind.Number)Check(actual.ValueKind==JsonValueKind.Number&&Math.Abs(actual.GetDouble()-check.Expected.GetDouble())<=check.Tolerance,"Expected numeric mismatch: "+check.NodeId+"."+check.Port+" "+actual);else Check(actual.GetRawText()==check.Expected.GetRawText(),"Expected value mismatch: "+check.NodeId+"."+check.Port+" "+actual);}
        if(manifest.Id==OperatorExpansionSamples.Ids[0])
        {var plane=run.Nodes["gradient_x"].Output<FloatPlaneFrame>("Derivative");using var lease=plane.Lease();var min=float.PositiveInfinity;var max=float.NegativeInfinity;for(var y=0;y<plane.Height;y++)foreach(var f in System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(lease.GetRowSpan(y))){min=Math.Min(min,f);max=Math.Max(max,f);}Check(min<0&&max>0,"Sobel must retain signed raw derivatives");Check(run.Nodes["gradient_x"].Output<ImageFrame>("Preview").PixelFormat==PixelFormat.Mono8,"Preview must be explicit Mono8");}
    }
    private static async Task<int> Examples(string root)
    {
        var source=Path.Combine(root,"source");await OperatorExpansionSamples.SaveAllAsync(source);var registry=BuiltinRegistration.CreateRegistry();var store=new ProjectStore(registry);var packages=new ProjectPackageStore(registry);var outcomes=new List<object>();var baseline=FrameHandle.ActiveBytes;
        var console=ConsolePath();Check(File.Exists(console),"Build source Console first; launch this tool with the repository as cwd.");
        foreach(var id in OperatorExpansionSamples.Ids)
        {
            var path=Path.Combine(source,id,"project.json");var manifest=JsonSerializer.Deserialize<ExampleManifest>(await File.ReadAllTextAsync(Path.Combine(source,id,"manifest.json")),JsonDefaults.Options)!;var project=await store.LoadAsync(path);string snapshot;Dictionary<string,string> hashes;
            using(var run=await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(),Path.GetDirectoryName(path)!)){Verify(run,manifest);snapshot=Snapshot(run);hashes=BufferHashes(run);}
            var package=Path.Combine(root,id+".vspkg");await packages.ExportAsync(path,package);var inspected=await packages.InspectAsync(package);Check(inspected.CanImport,string.Join(";",inspected.Problems));var relocated=await packages.ImportAsync(package,new(Path.Combine(root,"relocated",id)));
            using(var run=await new RuntimeExecutor(registry).ExecuteAsync((await store.LoadAsync(relocated)).ToGraph(),Path.GetDirectoryName(relocated)!)){Verify(run,manifest);await File.WriteAllTextAsync(Path.Combine(root,id+"-baseline-snapshot.json"),snapshot);await File.WriteAllTextAsync(Path.Combine(root,id+"-relocated-snapshot.json"),Snapshot(run));Check(Snapshot(run)==snapshot,"Package relocation changed structured output: "+id);Check(hashes.OrderBy(p=>p.Key).SequenceEqual(BufferHashes(run).OrderBy(p=>p.Key)),"Relocation changed full image/Float32 pixels: "+id);}
            await using(var host=new RuntimeHostSession(registry))
            {
                HostRequest Request(string operation,object? payload=null)=>new(){RequestId=Guid.NewGuid().ToString("N"),Operation=operation,Payload=payload is null?null:JsonDefaults.Element(payload)};
                var loaded=await host.HandleAsync(Request("load",new{ProjectPath=relocated}));Check(loaded.Success,JsonSerializer.Serialize(loaded));var start=await host.HandleAsync(Request("run"));Check(start.Success,JsonSerializer.Serialize(start));var accepted=start.Payload!.Value.Deserialize<HostRunAccepted>(HostJson.Options)!;
                HostResponse? result=null;for(var i=0;i<500;i++){result=await host.HandleAsync(Request("result",new RunReference(accepted.RunId)));if(result.Success)break;await Task.Delay(20);}Check(result?.Success==true,"Host result timeout");var hosted=result!.Payload!.Value.Deserialize<HostResultSnapshot>(HostJson.Options)!;Check(hosted.Success,"Host run failed");Check(HostSnapshot(hosted)==snapshot,"Host bounded numerical output differs: "+id);await Report(root,id+"-host.json",result);
            }
            var info=new ProcessStartInfo(console){WorkingDirectory=root,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};info.ArgumentList.Add("run");info.ArgumentList.Add(relocated);info.Environment["VISIONSTUDIO_MACHINE_CONFIG"]=Path.Combine(root,"unconfigured-machine.json");using(var process=Process.Start(info)??throw new IOException("Could not start own Console")){var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));try{await process.WaitForExitAsync(timeout.Token);}catch{if(!process.HasExited)process.Kill(true);throw;}await File.WriteAllTextAsync(Path.Combine(root,id+"-console.log"),await stdout);await File.WriteAllTextAsync(Path.Combine(root,id+"-console-errors.log"),await stderr);Check(process.ExitCode==0,"Actual Console execution failed: "+id);}
            outcomes.Add(new{Id=id,Runtime=true,Host=true,ActualConsole=true,Package=true,StructuredValuesMatch=true,FullBufferHashMatchAfterRelocation=true,BufferHashes=hashes,Relocated=relocated});Check(FrameHandle.ActiveBytes==baseline,"Owned image buffer leaked after example: "+id);
        }
        await Report(root,"examples-summary.json",new{Passed=true,Examples=outcomes,FrameBytesBefore=baseline,FrameBytesAfter=FrameHandle.ActiveBytes,UserDataUsed=false,Python=false,Hardware=false,Communication=false,HostComparisonScope="All bounded typed output snapshots; complete raster bytes also compared across local package relocation, Host protocol intentionally does not transfer all raster pixels."});Console.WriteLine("Four self-authored examples / real Console / Host / packages / buffer release passed.");return 0;
    }
}

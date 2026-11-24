using System.IO;
using System.Text.Json;
using System.Diagnostics;
using OpenCvSharp;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using VisionPlatform.Hosting;

internal static partial class Program
{
    private static partial async Task<int> Other(string command,string root,string[] args)
    {
        if(command=="ui")return Ui(root,args[2]);
        if(command=="examples"){await Examples(root);return 0;}
        if(command=="faults"){await Faults(root,Path.GetFullPath(args[2]));return 0;}
        if(command=="lifecycle"){await Lifecycle(root,args);return 0;}
        if(command=="stress"){await Stress(root,args);return 0;}
        throw new ArgumentException("commands: bench, examples, faults, stress, ui");
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static Task Report(string root,string file,object value)
    {
        var data=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject();
        data["BuildSourceHash"]=VisionPlatform.Contracts.Build.ProductBuild.SourceHash;
        data["ProductVersion"]=VisionPlatform.Contracts.Build.ProductBuild.Version;
        return File.WriteAllTextAsync(Path.Combine(root,file),data.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
    }
    private static string Snapshot(RunResult run)=>JsonSerializer.Serialize(run.Nodes.Where(n=>n.Key is "filter" or "each" or "count" or "result" or "circles").Select(n=>new{Node=n.Key,Status=n.Value.Status,Collections=n.Value.Outputs.Values.OfType<VisionCollection>().Select(c=>new{c.Kind,Rows=c.Items.Select(i=>new{i.Origin.SourceIndex,Fields=i.Fields.Where(f=>f.Key!="Milliseconds").ToDictionary(f=>f.Key,f=>f.Value),State=i.State}).ToArray()}).ToArray(),Count=n.Value.Outputs.GetValueOrDefault("Count"),IsOk=n.Value.Outputs.GetValueOrDefault("IsOk")}));
    private static async Task Examples(string root)
    {
        var source=Path.Combine(root,"source");await InspectionExampleFactory.SaveAllAsync(source);
        var registry=BuiltinRegistration.CreateRegistry();var store=new ProjectStore(registry);var packages=new ProjectPackageStore(registry);var rows=new List<object>();
        for(var index=0;index<3;index++)
        {
            var id=InspectionExampleFactory.Ids[index];var file=Path.Combine(source,id,"project.json");var document=await store.LoadAsync(file);
            string canonical;
            using(var run=await new RuntimeExecutor(registry).ExecuteAsync(document.ToGraph(),Path.GetDirectoryName(file)!))
            {
                Check(run.Success,run.ErrorMessage??"Example failed");var collection=run.Nodes[index==2?"each":"filter"].Output<VisionCollection>(index==2?"Results":"Collection");
                if(index<2)Check(collection.Count==2,"Expected exactly2 synthetic regions.");else Check(collection.Count>0&&run.Nodes["circles"].Output<int>("Count")>0,"No real geometric detection.");
                if(index==0)Check(!run.Nodes["result"].Output<bool>("IsOk"),"Two intentional differences must be NG.");canonical=Snapshot(run);
            }
            var package=Path.Combine(root,id+".vspkg");await packages.ExportAsync(file,package);Check((await packages.InspectAsync(package)).CanImport,"Package preflight failed.");
            var imported=await packages.ImportAsync(package,new(Path.Combine(root,"relocated",id)));
            using(var rerun=await new RuntimeExecutor(registry).ExecuteAsync((await store.LoadAsync(imported)).ToGraph(),Path.GetDirectoryName(imported)!))
            {Check(rerun.Success,rerun.ErrorMessage??"Relocated graph failed");Check(Snapshot(rerun)==canonical,"Relocated numerical outputs changed.");}
            await using var host=new RuntimeHostSession(registry);var load=await host.HandleAsync(new(){RequestId=Guid.NewGuid().ToString("N"),Operation="load",Payload=JsonDefaults.Element(new{ProjectPath=imported})});Check(load.Success,JsonSerializer.Serialize(load));
            var start=await host.HandleAsync(new(){RequestId=Guid.NewGuid().ToString("N"),Operation="run"});Check(start.Success,JsonSerializer.Serialize(start));
            var accepted=start.Payload!.Value.Deserialize<HostRunAccepted>(HostJson.Options)!;
            HostResponse? result=null;for(var attempt=0;attempt<300;attempt++){result=await host.HandleAsync(new(){RequestId=Guid.NewGuid().ToString("N"),Operation="result",Payload=JsonDefaults.Element(new RunReference(accepted.RunId))});if(result.Success)break;await Task.Delay(20);}
            Check(result!.Success,"Host did not produce result.");Check(result.Payload!.Value.Deserialize<HostResultSnapshot>(HostJson.Options)!.Status==NodeExecutionStatus.Success,"Host execution failed.");await Report(root,id+"-host.json",result);rows.Add(new{Id=id,Imported=imported,Runtime=true,Host=true,Package=true,SameNumerics=true});
        }
        // Actual 81,920,000-pixel file, read back and process a small zero-copy view.
        var before=FrameHandle.ActiveBytes;var clock=Stopwatch.StartNew();
        using(var b=new ImageFrameBuilder(8192,10000,PixelFormat.Mono8)){for(var y=0;y<10000;y++)b.GetRowSpan(y).Fill((byte)(y%256));using var f=b.Build();ControlledTiffChannel.Write(f,Path.Combine(root,"mono8192x10000.tif"),default);}
        var createMs=clock.Elapsed.TotalMilliseconds;clock.Restart();object large;
        using(var image=VisionPlatform.Imaging.ImageFile.Load(Path.Combine(root,"mono8192x10000.tif")))
        {var loadMs=clock.Elapsed.TotalMilliseconds;using var view=image.CreateView(100,200,320,240);Check(ReferenceEquals(image.BufferHandle,view.BufferHandle),"ROI copied full frame.");clock.Restart();var stats=InspectionProcessors.Statistics(view,null,null,default);using var blur=GaussianBlurProcessor.Process(view,new GaussianBlurOptions{KernelSize=5});large=new{Width=image.Width,Height=image.Height,Format=image.PixelFormat.ToString(),CreateMilliseconds=createMs,LoadMilliseconds=loadMs,RoiMilliseconds=clock.Elapsed.TotalMilliseconds,RoiBytes=blur.BufferHandle.Length,SourceResidentBytes=image.BufferHandle.Length,Stats=stats,WholeFrameCopiesPerItem=0};}
        Check(FrameHandle.ActiveBytes==before,"Large frame retained after scope.");await Report(root,"3b-examples-summary.json",new{Passed=true,Examples=rows,Large=large,ActiveFrameBytes=FrameHandle.ActiveBytes,ModelsIncluded=false,UserDataUsed=false});Console.WriteLine("3B examples, relocated packages, Host and 8192x10000 ROI passed.");
    }
    private static async Task Faults(string root,string python)
    {
        var script=Path.Combine(root,"resident_worker.py");await File.WriteAllTextAsync(script,"""
import argparse,json,sys,time
p=argparse.ArgumentParser();p.add_argument('--root');p.add_argument('--epoch');a=p.parse_args()
def emit(**kw):print(json.dumps(dict(protocol=2,epoch=a.epoch,**kw)),flush=True)
emit(ready=True,pid=__import__('os').getpid())
for line in sys.stdin:
 q=json.loads(line);rid=q['request_id'];mode=q.get('request',{}).get('mode','ok')
 if mode=='crash':sys.exit(9)
 if mode=='sleep':time.sleep(3)
 if mode=='wrongid':rid='stale'
 if mode=='wrongepoch':print(json.dumps(dict(protocol=2,epoch='stale',request_id=rid)),flush=True);continue
 if mode=='error':emit(request_id=rid,error='intentional controlled test');continue
 if q['operation']=='health':emit(request_id=rid,healthy=True);continue
 if q['operation']=='unload':emit(request_id=rid,unloaded=True);continue
 r=q['request']
 emit(request_id=rid,result=dict(timings={},request_id=rid,run_id=r['run_id'],image_frame_id='stale' if mode=='wrongframe' else r['image_frame_id'],model_sha256=r['expected_sha256']),model_loads=1,cached_models=1)
""");
        var requestFolder=Path.Combine(root,"VisionPlatform-Yolo-transport-test");Directory.CreateDirectory(requestFolder);var requestPath=Path.Combine(requestFolder,"request.json");var rows=new List<object>();
        await using(var bridge=new ResidentYoloBridge())
        {
            async Task<string> Send(string mode,int timeout=3000,CancellationToken ct=default){await File.WriteAllTextAsync(requestPath,JsonSerializer.Serialize(new{request_id=Guid.NewGuid().ToString("N"),run_id="controlled-fixture",image_frame_id="fixture-frame",expected_sha256="fixture-hash",mode}),ct);return await bridge.RunAsync(python,Path.Combine(root,"yolo_bridge.py"),requestPath,timeout,ct);}
            await Send("ok");var epoch=bridge.Epoch;await Send("ok");Check(epoch==bridge.Epoch,"Process not reused.");await bridge.HealthAsync();await bridge.UnloadAsync();
            foreach(var mode in new[]{"wrongid","wrongepoch","wrongframe","error","crash","sleep"})
            {
                var rejected=false;try{await Send(mode,mode=="sleep"?150:3000);}catch(Exception ex){rejected=true;rows.Add(new{Mode=mode,Type=ex.GetType().Name,ex.Message});if(mode=="sleep")Check(ex is TimeoutException,"Timeout not distinguished.");}
                Check(rejected&&bridge.ProcessId is null,"Bad response was accepted or worker retained.");await Send("ok");Check(epoch!=bridge.Epoch,"Restart reused stale epoch.");epoch=bridge.Epoch;
            }
            using(var cancel=new CancellationTokenSource(150)){try{await Send("sleep",3000,cancel.Token);throw new Exception("Cancel was ignored");}catch(OperationCanceledException){rows.Add(new{Mode="cancel",Passed=true});}}
            Check(bridge.ProcessId is null,"Cancelled service retained");await Send("ok");
            using var first=await bridge.AcquireFrameBudgetAsync(YoloQueuePolicy.Wait,1000,default);using var second=await bridge.AcquireFrameBudgetAsync(YoloQueuePolicy.Wait,1000,default);
            try{using var impossible=await bridge.AcquireFrameBudgetAsync(YoloQueuePolicy.Reject,1000,default);throw new Exception("Full queue accepted frame");}catch(InvalidOperationException){rows.Add(new{Mode="bounded-reject",Passed=true});}
            using(var cancellation=new CancellationTokenSource(50)){try{using var impossible=await bridge.AcquireFrameBudgetAsync(YoloQueuePolicy.Wait,1000,cancellation.Token);throw new Exception("Queued cancellation ignored");}catch(OperationCanceledException){rows.Add(new{Mode="queued-cancel",Passed=true});}}
        }
        await Report(root,"3a-transport-faults.json",new{Passed=true,TransportFixtureOnly=true,FakeEngineNeverUsedForAccuracy=true,Rows=rows});Console.WriteLine("3A private-process identity/fault/timeout/cancel/queue tests passed (transport fixture).");
    }
}

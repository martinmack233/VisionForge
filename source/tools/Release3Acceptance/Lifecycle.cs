using System.IO;
using System.Text.Json;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;

internal static partial class Program
{
    private static async Task Lifecycle(string root,string[] args)
    {
        var component=Path.GetFullPath(args[2]);var python=Path.GetFullPath(args[3]);var model=Path.GetFullPath(args[4]);var input=Path.GetFullPath(args[5]);var verified=await YoloDeploymentVerifier.VerifyAsync(model);
        var relocated=Path.Combine(root,"same-content-model-relocation");Directory.CreateDirectory(relocated);var copy=Path.Combine(relocated,Path.GetFileName(model));File.Copy(model,copy,false);File.Copy(Path.Combine(Path.GetDirectoryName(model)!,"deployment.json"),Path.Combine(relocated,"deployment.json"),false);
        var options=new YoloInferenceOptions{PythonExecutable=python,YoloStudioRoot=component,ModelPath=model,ImageSize=verified.InputSize,ExpectedModelSha256=verified.ModelSha256,ExpectedDeploymentSha256=verified.DeploymentSha256};var rows=new List<object>();
        using(var image=ImageFile.Load(input))await using(var session=new RuntimeExecutionSession())
        {
            var executor=new RuntimeExecutor(BuiltinRegistration.CreateRegistry(),null,session);var inputs=new Dictionary<string,object>{{"Image",image}};
            async Task<RunResult> Infer(YoloInferenceOptions p,CancellationToken ct=default)=>await executor.ExecuteSingleAsync(CollectionSubflows.N("yolo","YoloInference",p),inputs,root,ct);
            using(var first=await Infer(options)){Check(first.Success,first.ErrorMessage??"Initial engine failed");}
            Check(session.TryGet<ResidentYoloBridge>(ResidentYoloBridge.ServiceKey,out var bridge),"Resident missing");var epoch=bridge!.Epoch;
            using(var switched=await Infer(options with{ModelPath=copy})){Check(switched.Success,switched.ErrorMessage??"Model relocation failed");}
            var health=await bridge.HealthAsync();Check(health.GetProperty("cached_models").GetInt32()==1&&health.GetProperty("model_loads").GetInt32()==2,"Model identity switch not bounded/reloaded");rows.Add(new{Operation="same-content-relocated-model-switch",Health=health});
            await bridge.UnloadAsync();Check((await bridge.HealthAsync()).GetProperty("cached_models").GetInt32()==0,"Unload retained cache");using(var reloaded=await Infer(options)){Check(reloaded.Success,reloaded.ErrorMessage??"Reload failed");}
            using(var cancel=new CancellationTokenSource())
            {
                var running=Infer(options with{Tiled=true,TileWidth=128,TileHeight=128},cancel.Token);for(var i=0;i<2000&&bridge.QueueDepth==0&&!running.IsCompleted;i++)await Task.Delay(1);
                Check(bridge.QueueDepth>0,"No active request for cancellation test");cancel.Cancel();using var cancelled=await running;Check(cancelled.Status==NodeExecutionStatus.Cancelled,"In-flight cancellation not propagated");Check(bridge.ProcessId is null,"Owned worker survived cancelled request");rows.Add(new{Operation="real-inflight-cancel",Status=cancelled.Status.ToString()});
            }
            using(var restarted=await Infer(options)){Check(restarted.Success,restarted.ErrorMessage??"Restart failed");Check(bridge.Epoch!=epoch,"Restart reused stale epoch");var result=restarted.Nodes["yolo"].Output<YoloInferenceResult>("Result");Check(result.RunId==restarted.RunId&&result.FrameId==image.FrameId,"Restart returned stale run/frame");}
            rows.Add(new{Operation="explicit-restart",EpochChanged=true,Cache=await bridge.HealthAsync()});
        }
        Check(FrameHandle.ActiveBytes==0,"Lifecycle leaked image resources");await Report(root,"3a-real-lifecycle.json",new{Passed=true,Rows=rows,ModelSwitchScope="Same approved model/hash relocated to a separate local path; no claim about a second different trained model",TrainingStarted=false,GpuUsed=false});Console.WriteLine("3A real ONNX model relocation/unload/in-flight cancellation/restart passed.");
    }
}

using System.IO;
using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.Acquisition;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;

internal static partial class Program
{
    private static async Task Stress(string root,string[] args)
    {
        var component=Path.GetFullPath(args[2]);var python=Path.GetFullPath(args[3]);var model=Path.GetFullPath(args[4]);var input=Path.GetFullPath(args[5]);
        var seconds=args.Length>6?int.Parse(args[6]):20;if(seconds is <1 or >300)throw new ArgumentException("Finite stress duration must be1..300seconds; hourly tests require separate scheduling.");
        var folder=Path.Combine(root,"virtual-frames");Directory.CreateDirectory(folder);File.Copy(input,Path.Combine(folder,"frame.png"),false);var verified=await YoloDeploymentVerifier.VerifyAsync(model);var registry=BuiltinRegistration.CreateRegistry();var rows=new List<object>();var times=new List<double>();
        var options=new YoloInferenceOptions{PythonExecutable=python,YoloStudioRoot=component,ModelPath=model,ImageSize=verified.InputSize,ExpectedModelSha256=verified.ModelSha256,ExpectedDeploymentSha256=verified.DeploymentSha256};
        var graph=new NodeGraph([CollectionSubflows.N("camera","VirtualCamera",new VirtualCameraParameters{FolderPath=folder}),CollectionSubflows.N("blur","GaussianBlur",new GaussianBlurParameters()),CollectionSubflows.N("yolo","YoloInference",options),CollectionSubflows.N("adapt","CollectionAdapter",new{}),CollectionSubflows.N("each","ForEach",new ForEachOptions{Body=CollectionSubflows.Measure(),DebugIterations=1}),CollectionSubflows.N("count","CollectionCount",new{}),CollectionSubflows.N("judge","Compare",new{ValueB=0d,Operator="GreaterOrEqual"}),CollectionSubflows.N("result","ResultDisplay",new{})],[new("camera","Image","blur","Image"),new("blur","ProcessedImage","yolo","Image"),new("yolo","Result","adapt","Detections"),new("adapt","Collection","each","Collection"),new("each","Results","count","Collection"),new("count","Count","judge","ValueA"),new("judge","Result","result","IsOk"),new("count","Count","result","BlobCount")]);
        int? lastPid=null;var ids=new HashSet<Guid>();var clock=Stopwatch.StartNew();
        await using(var session=new RuntimeExecutionSession())
        {
            var executor=new RuntimeExecutor(registry,null,session);string? epoch=null;
            while(clock.Elapsed.TotalSeconds<seconds&&rows.Count<1000)
            {
                using var run=await executor.ExecuteAsync(graph,root);Check(run.Success,run.ErrorMessage??"Stress flow failed");var frame=run.Nodes["camera"].Output<ImageFrame>("Image");var yolo=run.Nodes["yolo"].Output<YoloInferenceResult>("Result");var blurred=run.Nodes["blur"].Output<ImageFrame>("ProcessedImage");
                Check(ids.Add(frame.FrameId)&&yolo.FrameId==blurred.FrameId&&yolo.RunId==run.RunId,"Wrong/stale run/frame identity");
                var info=run.Nodes["camera"].Output<CameraFrameInfo>("FrameInfo");Check(info.FrameId==frame.FrameId&&info.Sequence==rows.Count+1,"Acquisition sequence not current");
                Check(session.TryGet<ResidentYoloBridge>(ResidentYoloBridge.ServiceKey,out var service),"Resident scope missing");lastPid=service!.ProcessId;
                var parent=Process.GetCurrentProcess();parent.Refresh();using var worker=Process.GetProcessById(lastPid!.Value);worker.Refresh();var ms=run.ExecutionTime.TotalMilliseconds;times.Add(ms);
                rows.Add(new{Index=rows.Count,info.Sequence,SourceFrame=frame.FrameId,ProcessingFrame=blurred.FrameId,ResultFrame=yolo.FrameId,run.RunId,FlowMilliseconds=ms,ParentWorkingSet=parent.WorkingSet64,ParentHandles=parent.HandleCount,WorkerWorkingSet=worker.WorkingSet64,WorkerHandles=worker.HandleCount,service.QueueDepth,Timings=yolo.Timings});
                // Simulate crash of exactly our owned worker, then require a fresh epoch on the next explicit run.
                if(rows.Count==5){epoch=service.Epoch;worker.Kill(true);await worker.WaitForExitAsync();}
                if(rows.Count==6)Check(epoch!=service.Epoch,"Crashed service reused epoch");
            }
            Check(session.TryGet<ResidentYoloBridge>(ResidentYoloBridge.ServiceKey,out var resident),"Resident missing");var health=await resident!.HealthAsync();Check(health.GetProperty("cached_models").GetInt32()==1,"Unbounded model cache");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();using var run=await executor.ExecuteAsync(graph,root,cancelled.Token);Check(!run.Success,"Precancelled run executed successfully");}
            var cameras=CameraSession.For(session);await cameras.CloseDeviceAsync("virtual-main");
        }
        var stopped=true;if(lastPid is{} pid){try{using var p=Process.GetProcessById(pid);stopped=p.HasExited;}catch(ArgumentException){}}
        Check(stopped&&FrameHandle.ActiveBytes==0,"Owned process/frame survived stop");var hot=times.Skip(1).Order().ToArray();double Q(double q)=>hot[(int)Math.Ceiling(q*hot.Length)-1];
        await Report(root,"3c-finite-stress.json",new{Passed=true,DurationSeconds=clock.Elapsed.TotalSeconds,Runs=rows.Count,ColdMilliseconds=times[0],P50=hot.Length>0?Q(.5):(double?)null,P95=hot.Length>=20?Q(.95):(double?)null,P99=hot.Length>=100?Q(.99):(double?)null,ThroughputIncludingCold=rows.Count/clock.Elapsed.TotalSeconds,FrameBytesAfterStop=FrameHandle.ActiveBytes,OwnedProcessStopped=stopped,CrashRestart=true,QueuePolicy="Sequential per-frame producer plus resident capacity2; no frame replacement",RealCamera=false,AllDayVerified=false,Rows=rows});Console.WriteLine("3C finite virtual-camera/YOLO/collection/decision stress passed.");
    }
}

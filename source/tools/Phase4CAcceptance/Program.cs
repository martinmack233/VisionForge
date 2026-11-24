using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Hosting;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

internal static class Program
{
    private static string _evidence="";
    [STAThread] private static int Main(string[] args)
    {
        var root=Path.GetFullPath(args.FirstOrDefault()??".");_evidence=Path.Combine(root,"artifacts/phase4c-acceptance");Directory.CreateDirectory(_evidence);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml")});
        var exit=1;app.Startup+=async(_,_)=>{try{await Verify(root,args.ElementAtOrDefault(1));exit=0;}catch(Exception e){Console.Error.WriteLine(e);await Save("summary.json",new{Passed=false,Error=e.ToString()});}finally{app.Shutdown(exit);}};
        app.Run();return exit;
    }
    private static void Check(bool ok,string message)=>ExampleRuns.Check(ok,message);
    private static Task Save(string name,object data)=>File.WriteAllTextAsync(Path.Combine(_evidence,name),JsonSerializer.Serialize(data,JsonDefaults.Options));
    private static async Task Verify(string root,string? yoloProject)
    {
        await Phase4CExampleFactory.SaveAllAsync(Path.Combine(root,"Examples"));
        var registry=BuiltinRegistration.CreateRegistry();var store=ExampleRuns.Store(registry);var catalog=new ExampleCatalog(Path.Combine(root,"Examples"),store);var entries=await catalog.LoadAsync();
        Check(entries.Count==15&&catalog.LoadErrors.Count==0,"Expected 15 complete examples.");
        var hashes=await HashFiles(Path.Combine(root,"Examples"));
        foreach(var entry in entries)Check((await catalog.ValidateAsync(entry)).Count==0,"Invalid example "+entry.Manifest.Id);
        var headless=await ExampleRuns.Headless(catalog,entries.Where(e=>e.Manifest.Id!="yolo-external").ToArray(),registry);
        var tcp=await ExampleRuns.Tcp(entries.Single(e=>e.Manifest.Id=="tcp-loopback"),registry);
        var baseline=FrameHandle.ActiveBytes;var state=Path.Combine(_evidence,"workspace");
        await new WorkspaceStateStore(state).SaveAsync(new(1,new WorkspaceSettings{ExamplesDirectory=Path.Combine(root,"Examples"),DefaultProjectDirectory=Path.Combine(_evidence,"user-projects"),LogDirectory=Path.Combine(_evidence,"logs"),LogEnabled=false},[]));
        var session=new EditorSession();var window=new MainWindow(session,state);window.Show();await window.WorkspaceReady;
        Check(session.Results.Count==0&&FrameHandle.ActiveBytes==baseline,"Dashboard unexpectedly captured or ran algorithms.");
        Check(window.Devices.Devices.Count==1&&window.Devices.PreviewImage is null,"Device enumeration must not start capture.");
        window.Navigate(WorkspacePage.Devices);await Capture(window,"devices-closed.png");
        window.Navigate(WorkspacePage.Examples);window.Examples.Selected=window.Examples.Items.Single(e=>e.Manifest.Id=="virtual-camera");await window.Examples.RunSelectedAsync();
        Check(window.Examples.LastVerification?.Passed==true,window.Examples.Status+window.Examples.LastFailureDetails);
        Check(((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence==1,"Runtime camera did not start at sequence 1.");
        await ExampleRuns.ConsoleRun(root,session.ProjectPath!,Path.Combine(_evidence,"console","virtual-camera"));
        window.Navigate(WorkspacePage.Devices);await window.Devices.OpenAsync();
        Check(window.Devices.Parameters.All(p=>p.Capability.Name is not("Exposure" or "Gain")),"Virtual camera fabricated hardware capabilities.");
        await window.Devices.CaptureAsync();Check(window.Devices.FrameInfo?.Sequence==2,"UI did not reuse the Runtime camera device.");
        await window.DevicePage.PreviewViewport.WaitForDisplayAsync();await Capture(window,"devices-single.png");
        await window.Devices.RunProjectAsync();
        Check(((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence==3&&!(bool)session.CurrentOutputs["match"]["Success"],"Detection reused preview instead of capturing next/absent frame.");
        Check(session.Results.Single(r=>r.NodeId=="roi").Status=="Skipped"&&session.Results.Single(r=>r.NodeId=="blob").Status=="Skipped","Absent frame reused old followed ROI/blob.");
        await window.Devices.RunProjectAsync();Check((bool)session.CurrentOutputs["decision"]["IsOk"],"Loop did not return to the first actual frame.");
        session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="capture");session.InspectOutput("capture","FrameInfo");window.FitEditorViews();await Capture(window,"camera-runtime.png");
        window.Navigate(WorkspacePage.Devices);await window.Devices.StartAsync();
        await Until(()=>window.Devices.FramesReceived>=5,TimeSpan.FromSeconds(8));await Capture(window,"devices-continuous.png");
        await window.Devices.StopAsync();Check(!window.Devices.IsAcquiring&&window.Devices.PreviewImage is null,"Stop retained a preview or acquisition loop.");
        await window.Devices.ConfigureNodeAsync();
        var copied=session.ProjectPath!;await session.SaveAsync();await session.OpenAsync(copied);await session.RunAsync();
        Check(((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence==1&&(bool)session.CurrentOutputs["decision"]["IsOk"],"Saved camera project did not reopen reproducibly.");
        window.Navigate(WorkspacePage.Devices);await window.Devices.OpenAsync();await window.Devices.StartAsync();await Until(()=>window.Devices.PreviewImage is not null,TimeSpan.FromSeconds(5));
        await session.NewAsync();await window.Devices.StopAsync();Check(window.Devices.PreviewImage is null&&!window.Devices.IsAcquiring&&window.Devices.DeviceState=="Closed","Project switch retained old device/preview.");
        var large=await LargeCamera(root,window);
        var oldProjects=new[]{"tests/Fixtures/legacy-editor-project.json","samples/phase3a/flow1.json","samples/phase3a/flow2.json","samples/phase3a/flow3.json","samples/phase3b/flow1.json","samples/phase3c/flow6-enhance-transform.json"};
        foreach(var old in oldProjects){await window.OpenProjectAsync(Path.Combine(root,old),true);await session.RunAsync();Check(session.Results.Count==session.Nodes.Count&&session.Results.All(r=>r.Status is "Success" or "Skipped"),"Old project failed: "+old);}
        foreach(var id in new[]{"runtime-host","host-tcp"})
        {
            window.Examples.Selected=window.Examples.Items.Single(e=>e.Manifest.Id==id);await window.Examples.RunSelectedAsync();Check(window.Examples.LastVerification?.Passed==true,id+window.Examples.Status);
            await ExampleRuns.ConsoleRun(root,session.ProjectPath!,Path.Combine(_evidence,"console",id));
        }
        await session.NewAsync();await window.CloseEditorAsync(true);await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
        Check(FrameHandle.ActiveBytes==baseline&&window.DevicePage.PreviewViewport.DisplayCacheBytes==0,"Desktop close leaked images/tiles.");
        var host=await HostFlows(root,entries,yoloProject);
        var after=await HashFiles(Path.Combine(root,"Examples"));Check(hashes.OrderBy(p=>p.Key).SequenceEqual(after.OrderBy(p=>p.Key)),"Built-in examples were modified.");
        var authoring=new VisionPlatform.Editor.Core.Catalog.NodeAuthoringCatalog();
        await File.WriteAllTextAsync(Path.Combine(root,"docs/第四阶段C节点接口.json"),JsonSerializer.Serialize(registry.RegisteredTypeIds.Order().Select(id=>new{TypeId=id,authoring.Get(id).DisplayName,authoring.Get(id).Category,Parameters=authoring.Get(id).DefaultParameters,Inputs=registry.Create(id).Descriptor.InputPorts.Select(p=>new{p.Name,Type=p.DataType.Name,p.Required}),Outputs=registry.Create(id).Descriptor.OutputPorts.Select(p=>new{p.Name,Type=p.DataType.Name,p.Required})}),JsonDefaults.Options));
        await Save("summary.json",new{Passed=true,Headless=headless,Tcp=tcp,OldProjects=oldProjects,DesktopCamera=true,CameraNextFrameSemantics=true,FailureClearsRoi=true,SaveReload=true,ProjectSwitchStopsCapture=true,LargeCamera=large,Host=host,RegisteredNodes=registry.RegisteredTypeIds.Count,Examples=entries.Count,BuffersBefore=baseline,BuffersAfter=FrameHandle.ActiveBytes,CacheAfterClose=window.DevicePage.PreviewViewport.DisplayCacheBytes,BuiltInExamplesUnchanged=true,ConsoleWpfHostSameRuntime=true,PhysicalCameraTested=false,PhysicalSerialTested=false,AllDayStabilityTested=false,Phase5Started=false});
        Console.WriteLine("Phase4C acceptance passed.");
    }
    private static async Task<object> LargeCamera(string root,MainWindow window)
    {
        var dir=Path.Combine(_evidence,"large-camera");Directory.CreateDirectory(dir);
        await Task.Run(()=>File.Copy(Path.Combine(root,"samples/phase3c/large-8192x10000.bmp"),Path.Combine(dir,"large.bmp"),true));
        var vm=window.Devices;vm.FolderPath=dir;vm.CameraId="large-camera";vm.FrameRateText="2";vm.Loop=false;vm.PixelFormatChoice="Mono8";
        var watch=Stopwatch.StartNew();await vm.OpenAsync();var openMs=watch.Elapsed.TotalMilliseconds;watch.Restart();var capture=vm.CaptureAsync();
        var heartbeat=Stopwatch.StartNew();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);var heartbeatMs=heartbeat.Elapsed.TotalMilliseconds;
        await capture;var captureMs=watch.Elapsed.TotalMilliseconds;
        Check(vm.PreviewImage is{Width:8192,Height:10000,PixelFormat:PixelFormat.Mono8},"82 MP camera frame was not decoded.");
        window.Navigate(WorkspacePage.Devices);var viewport=window.DevicePage.PreviewViewport;viewport.ShowOneToOne();watch.Restart();await viewport.WaitForDisplayAsync();var previewMs=watch.Elapsed.TotalMilliseconds;
        Check(viewport.IsNativeResolutionReady&&viewport.DisplayCacheBytes<=viewport.MaxDisplayCacheBytes,"82 MP camera preview is not real 1:1 or exceeded cache budget.");
        await Capture(window,"devices-82mp.png");var bytes=FrameHandle.ActiveBytes;var cache=viewport.DisplayCacheBytes;
        await vm.CloseAsync();await viewport.WaitForDisplayAsync();Check(vm.PreviewImage is null,"Large camera did not release its reference.");
        var result=new{Passed=true,Width=8192,Height=10000,OpenMilliseconds=openMs,CaptureMilliseconds=captureMs,NativePreviewMilliseconds=previewMs,UiHeartbeatMilliseconds=heartbeatMs,PlatformFrameBytes=bytes,CacheBytes=cache,CacheLimit=viewport.MaxDisplayCacheBytes,TrueOneToOne=true};await Save("large-camera.json",result);return result;
    }
    private static HostRequest Request(string op,object? payload=null)=>new(){RequestId=Guid.NewGuid().ToString("N"),Operation=op,Payload=payload is null?null:JsonDefaults.Element(payload)};
    private static async Task<HostResultSnapshot> Run(RuntimeHostClient client,HostRequest? command=null)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(90));command??=Request("run",new RunProjectCommand());
        var accepted=await client.SendAsync(command,deadline.Token);Check(accepted.Success,accepted.Message);var run=accepted.Payload!.Value.Deserialize<HostRunAccepted>(JsonDefaults.Options)!;
        while(true)
        {
            var status=await client.SendAsync(Request("status",new RunReference(run.RunId)),deadline.Token);Check(status.Success,status.Message);
            if(status.Payload!.Value.Deserialize<HostStatusSnapshot>(JsonDefaults.Options)!.State!=HostSessionState.Running)break;
            await Task.Delay(20,deadline.Token);
        }
        var result=await client.SendAsync(Request("result",new RunReference(run.RunId)),deadline.Token);Check(result.Success,result.ErrorCode+result.Message);
        var snapshot=result.Payload!.Value.Deserialize<HostResultSnapshot>(JsonDefaults.Options)!;Check(snapshot.RunId==run.RunId&&snapshot.EngineRunId is not null,"Host/engine run identity missing.");return snapshot;
    }
    private static async Task<object> HostFlows(string root,IReadOnlyList<ExampleEntry> entries,string? yoloProject)
    {
        var baseline=FrameHandle.ActiveBytes;var registry=BuiltinRegistration.CreateRegistry();
        var plugin=Path.Combine(root,"tools/HostingFixturePlugin/bin/Release/net10.0/HostingFixturePlugin.dll");Check(TrustedPluginLoader.Load(registry,plugin)==1,"Explicit plugin was not discovered.");
        await using var server=new RuntimeHostServer(registry,new(){Port=0},new Dictionary<string,string>{["cvinspect.pattern"]="0.34.0+0eddfbaa"});await server.StartAsync();
        var snapshots=new List<object>();
        await using(var client=new RuntimeHostClient())
        {
            await client.ConnectAsync("127.0.0.1",server.BoundPort);
            foreach(var id in new[]{"runtime-host","host-tcp","ocr","qr-code","barcode","virtual-camera"})
            {
                var path=entries.Single(e=>e.Manifest.Id==id).ProjectPath;var load=await client.SendAsync(Request("load",new LoadProjectCommand(path)));Check(load.Success,load.Message);
                var command=Request("run",new RunProjectCommand());var run=await Run(client,command);Check(run.Success,"Host real graph failed: "+id);
                Check(!JsonSerializer.Serialize(run,JsonDefaults.Options).Contains("BufferHandle",StringComparison.Ordinal),"Native image handle leaked onto wire.");
                if(id=="virtual-camera")
                {
                    Check(FrameSequence(run)==1,"First camera run sequence differs.");var replay=await client.SendAsync(command);Check(replay.Payload!.Value.Deserialize<HostRunAccepted>(JsonDefaults.Options)!.RunId==run.RunId,"Duplicate run request was not idempotent.");
                    var second=await Run(client);Check(FrameSequence(second)==2&&second.Success,"Host reset camera service between runs.");
                    var stale=await client.SendAsync(Request("result",new RunReference(run.RunId)));Check(!stale.Success&&stale.ErrorCode==HostErrorCodes.RunNotFound,"Old-run result reused.");
                    var third=await Run(client);Check(FrameSequence(third)==3&&!third.Success&&third.Nodes.Single(n=>n.NodeId=="roi").Status==NodeExecutionStatus.Skipped,"Host absent frame reused ROI.");
                    await Save("host-camera-sequence.json",new[]{run,second,third});
                }
                snapshots.Add(new{Example=id,run.RunId,run.EngineRunId,run.Status,run.ExecutionMilliseconds,NodeCount=run.Nodes.Count});
            }
            if(yoloProject is not null)
            {
                var load=await client.SendAsync(Request("load",new LoadProjectCommand(Path.GetFullPath(yoloProject))));Check(load.Success,load.Message);
                var yolo=await Run(client);Check(yolo.Success&&yolo.Nodes.Single(n=>n.NodeId=="recognize").Outputs["Result"].Value.GetProperty("Detections").GetArrayLength()>0,"Host real external YOLO failed.");await Save("host-yolo-real.json",yolo);
            }
            var fixture=new ProjectDocument{ProjectName="ExplicitPlugin",Nodes=[new("fixture","HostingFixtureEcho","fixture",JsonDefaults.Element(new{}))]};var fixturePath=Path.Combine(_evidence,"plugin-project.json");await new ProjectStore(registry).SaveAsync(fixturePath,fixture);
            Check((await client.SendAsync(Request("load",new LoadProjectCommand(fixturePath)))).Success,"Host plugin project failed to load.");var pluginRun=await Run(client);Check(pluginRun.Nodes.Single().Outputs["Number"].Value.GetDouble()==42,"Actual plugin was not executed.");
            Check((await client.SendAsync(Request("close"))).Success,"Close failed.");
        }
        await Until(()=>server.ActiveConnections==0,TimeSpan.FromSeconds(5));await server.StopAsync();Check(FrameHandle.ActiveBytes==baseline,"Host close retained graph image buffers.");
        var data=new{Passed=true,ActualLoopback=true,Examples=snapshots,ActualPlugin=true,RealExternalYolo=yoloProject is not null,RepeatedRunIdempotent=true,CameraSequencePersists=true,StaleRunRejected=true,Stopped=true,BuffersBefore=baseline,BuffersAfter=FrameHandle.ActiveBytes};await Save("host-flows.json",data);return data;
    }
    private static long FrameSequence(HostResultSnapshot run)=>run.Nodes.Single(n=>n.NodeId=="capture").Outputs["FrameInfo"].Value.GetProperty("Sequence").GetInt64();
    private static async Task Until(Func<bool> predicate,TimeSpan timeout){var watch=Stopwatch.StartNew();while(!predicate()){if(watch.Elapsed>timeout)throw new TimeoutException("Acceptance condition did not complete.");await Task.Delay(20);}}
    private static async Task Capture(Window window,string name)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);var image=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(_evidence,name));encoder.Save(stream);
    }
    private static async Task<Dictionary<string,string>> HashFiles(string directory)
    {
        var data=new Dictionary<string,string>();foreach(var file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories)){await using var stream=File.OpenRead(file);data[Path.GetRelativePath(directory,file)]=Convert.ToHexString(await SHA256.HashDataAsync(stream));}return data;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VisionPlatform.Communication.Modbus;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Hosting;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
internal static partial class Program
{
    private static string ConsolePath()=>Environment.GetEnvironmentVariable("VISION_OPERATOR_CONSOLE")??Path.Combine(Environment.CurrentDirectory,"src/VisionPlatform.Console/bin/Release/net10.0/VisionPlatform.Console.exe");
    private static void VerifyCommunication(RunResult run,int index,bool writes=false)
    {
        Check(run.Success,string.Join(";",run.Nodes.Values.Where(n=>n.ErrorMessage is not null).Select(n=>n.ErrorMessage)));
        if(index==0){Check(run.Nodes["exchange"].Output<string>("Message")=="PONG,2","TCP actual message differs");Check(run.Nodes["exchange"].Output<bool>("ResponseReceived"),"No valid response");}
        else{Check(run.Nodes["value"].Output<double>("Number")==200,"Decoded selected register differs");Check(run.Nodes["read"].Output<ModbusRegisterValues>("Registers").SequenceEqual(new ushort[]{100,200,300}),"Raw registers differ");if(writes)Check(run.Nodes["readback"].Output<ModbusRegisterValues>("Registers").SequenceEqual(new ushort[]{1,2,65535}),"Explicit simulation write/readback differs");}
    }
    private static async Task<int> Communications(string root)
    {
        await OperatorCommunicationDemo.SaveAllAsync(Path.Combine(root,"source"));var registry=BuiltinRegistration.CreateRegistry();var store=new ProjectStore(registry);var packages=new ProjectPackageStore(registry);var outcomes=new List<object>();
        for(var i=0;i<2;i++)
        {
            var index=i;async Task Execute(int port,CancellationToken ct)
            {
                var project=OperatorCommunicationDemo.Project(index);var connect=project.Nodes.Single(n=>n.NodeId=="connect");object parameters=index==0?new TcpCommunicationOptions{Client=new(){Port=port},ConnectionId="owned-demo"}:new ModbusCommunicationOptions{Tcp=new(){Port=port},ConnectionId="owned-modbus-demo"};project.Nodes[project.Nodes.IndexOf(connect)]=connect with{Parameters=JsonDefaults.Element(parameters)};
                var source=Path.Combine(root,OperatorCommunicationDemo.Ids[index]+".json");await store.SaveAsync(source,project,ct);using(var run=await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(),root,cancellationToken:ct)){VerifyCommunication(run,index);await Report(root,OperatorCommunicationDemo.Ids[index]+"-runtime.json",new{Success=run.Success,Nodes=run.Nodes.Select(n=>new{n.Key,n.Value.Status,n.Value.ExecutionTime,Outputs=n.Value.Outputs.ToDictionary(p=>p.Key,p=>HostResultProjector.ProjectValue(p.Value))})});}
                var package=Path.Combine(root,OperatorCommunicationDemo.Ids[index]+".vspkg");await packages.ExportAsync(source,package,ct:ct);Check((await packages.InspectAsync(package,ct)).CanImport,"Communication package cannot import");var imported=await packages.ImportAsync(package,new(Path.Combine(root,"relocated",OperatorCommunicationDemo.Ids[index])),ct);
                await using(var host=new RuntimeHostSession(registry))
                {
                    HostRequest R(string op,object? payload=null)=>new(){RequestId=Guid.NewGuid().ToString("N"),Operation=op,Payload=payload is null?null:JsonDefaults.Element(payload)};
                    Check((await host.HandleAsync(R("load",new{ProjectPath=imported}))).Success,"Host load failed");var start=await host.HandleAsync(R("run"));Check(start.Success,"Host run rejected");var accepted=start.Payload!.Value.Deserialize<HostRunAccepted>(HostJson.Options)!;HostResponse? result=null;for(var n=0;n<500;n++){result=await host.HandleAsync(R("result",new RunReference(accepted.RunId)));if(result.Success)break;await Task.Delay(10,ct);}Check(result?.Success==true,"Host response timeout");var snapshot=result!.Payload!.Value.Deserialize<HostResultSnapshot>(HostJson.Options)!;Check(snapshot.Success,"Host communication execution failed");var actual=snapshot.Nodes.Single(n=>n.NodeId==(index==0?"exchange":"value")).Outputs.Single(p=>p.Key==(index==0?"Message":"Number")).Value.Value;Check(index==0?actual.GetString()=="PONG,2":actual.GetDouble()==200,"Host actual communication value differs");await Report(root,OperatorCommunicationDemo.Ids[index]+"-host.json",result);
                }
                var info=new ProcessStartInfo(ConsolePath()){WorkingDirectory=root,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};info.ArgumentList.Add("run");info.ArgumentList.Add(imported);info.Environment["VISIONSTUDIO_MACHINE_CONFIG"]=Path.Combine(root,"unconfigured-machine.json");using var process=Process.Start(info)??throw new IOException("Console start failed");var output=process.StandardOutput.ReadToEndAsync(ct);var errors=process.StandardError.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);var text=await output;await File.WriteAllTextAsync(Path.Combine(root,OperatorCommunicationDemo.Ids[index]+"-console.log"),text,ct);await File.WriteAllTextAsync(Path.Combine(root,OperatorCommunicationDemo.Ids[index]+"-stderr.log"),await errors,ct);Check(process.ExitCode==0,"Actual Console communication failed");Check(text.Contains(index==0?"PONG,2":"200",StringComparison.Ordinal),"Console actual communication value missing");
            }
            var report=index==0?await OperatorCommunicationDemo.RunTcpAsync(Execute,clients:3):await OperatorCommunicationDemo.RunModbusTcpAsync(Execute,clients:3);Check(report.Frames.Count==3,"Expected one actual transaction per Runtime/Host/Console");await Report(root,OperatorCommunicationDemo.Ids[index]+"-frames.json",report);outcomes.Add(new{Id=OperatorCommunicationDemo.Ids[index],Runtime=true,Host=true,ActualConsole=true,PackageRelocation=true,ActualTransactions=report.Frames.Count});
        }
        var writes=await OperatorCommunicationDemo.RunModbusTcpAsync(async(port,ct)=>{var project=OperatorCommunicationDemo.Project(1,true);var node=project.Nodes[0];var p=node.Parameters.Deserialize<ModbusCommunicationOptions>(JsonDefaults.Options)!;project.Nodes[0]=node with{Parameters=JsonDefaults.Element(p with{Tcp=p.Tcp with{Port=port}})};using var run=await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(),root,cancellationToken:ct);VerifyCommunication(run,1,true);});await Report(root,"modbus-explicit-simulated-write.json",writes);var rtu=await OperatorCommunicationDemo.RunRtuSimulationAsync();Check(rtu.Frames.Count==9&&!rtu.HardwareVerified,"RTU simulation not complete");await Report(root,"rtu-protocol-simulation.json",rtu);await Report(root,"communications-summary.json",new{Passed=true,Examples=outcomes,ExplicitSimulatedTcpWrite=true,RtuActualRuntime=true,RtuFrames=rtu.Frames.Count,LoopbackOnly=true,PhysicalHardware=false,ProductionCommunication=false});Console.WriteLine("Real loopback TCP/Modbus across Runtime, Host and Console, relocated packages, explicit simulated write and RTU protocol passed.");return 0;
    }
}

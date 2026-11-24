using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase4AExamplesTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"VisionStudio-Phase4AExamples-"+Guid.NewGuid().ToString("N"));
    private static ProjectStore Store()=>new(BuiltinRegistration.CreateRegistry(),new Dictionary<string,string>{["cvinspect.pattern"]="0.34.0+0eddfbaa"});
    private async Task<ExampleCatalog> Create()
    {
        var examples=Path.Combine(_root,"Examples");await Phase4AExampleFactory.SaveAllAsync(examples);return new(examples,Store());
    }
    [Fact]
    public async Task EightCompleteOfflineExamplesUseRealRuntimeAndAllExpectedImageCases()
    {
        var before=FrameHandle.ActiveBytes;var catalog=await Create();var entries=await catalog.LoadAsync();
        Assert.Equal(8,entries.Count);Assert.Empty(catalog.LoadErrors);
        foreach(var entry in entries)
        {
            Assert.Empty(await catalog.ValidateAsync(entry));
            var project=await Store().LoadAsync(entry.ProjectPath);
            foreach(var variant in entry.Manifest.Cases.Cast<ExampleCase?>().DefaultIfEmpty(null))
            {
                var current=variant is null?project:WithImage(project,variant.ImagePath);
                using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(current.ToGraph(),entry.Directory);
                var verified=ExampleVerifier.Verify(entry.Manifest,run,variant?.Id);
                Assert.True(verified.Passed,entry.Manifest.Id+"/"+variant?.Id+": "+verified.Summary);
                if(variant?.Id=="absent")
                {
                    Assert.False(run.Nodes["match"].Output<bool>("Success"));
                    Assert.Empty(run.Nodes["roi"].Outputs);Assert.Empty(run.Nodes["blob"].Outputs);
                }
            }
        }
        Assert.Equal(before,FrameHandle.ActiveBytes);
    }
    [Fact]
    public async Task CopyCreatesIndependentIdentityResourcesAndNeverMutatesBuiltInTemplate()
    {
        var catalog=await Create();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="template-follow");
        var original=await File.ReadAllBytesAsync(entry.ProjectPath);
        var copy=await catalog.CopyAsync(entry,Path.Combine(_root,"Projects"),"独立工程");
        var copied=await Store().LoadAsync(copy);var source=await Store().LoadAsync(entry.ProjectPath);
        Assert.Equal("独立工程",copied.ProjectName);Assert.NotEqual(source.ProjectId,copied.ProjectId);
        Assert.Equal(original,await File.ReadAllBytesAsync(entry.ProjectPath));
        using var result=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(copied.ToGraph(),Path.GetDirectoryName(copy)!);
        Assert.True(ExampleVerifier.Verify(entry.Manifest,result).Passed);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>catalog.CopyAsync(entry,catalog.Root));
        var another=await catalog.CopyAsync(entry,Path.Combine(_root,"Projects"));Assert.NotEqual(copy,another);
    }
    [Fact]
    public async Task MissingTamperedAndEscapingResourcesFailBeforeCopy()
    {
        var catalog=await Create();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="basic");
        var image=Path.Combine(entry.Directory,"images/input.png");await File.AppendAllTextAsync(image,"corruption");
        Assert.Contains(await catalog.ValidateAsync(entry),e=>e.Contains("资源校验失败"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>catalog.CopyAsync(entry,Path.Combine(_root,"Projects")));
        Assert.Throws<InvalidDataException>(()=>ExampleCatalog.SafeFile(entry.Directory,"../../outside.png"));
        Assert.Throws<InvalidDataException>(()=>ExampleCatalog.SafeFile(entry.Directory,Path.GetFullPath(image)));
        File.Delete(image);Assert.Contains(await catalog.ValidateAsync(entry),e=>e.Contains("缺少资源"));
    }
    [Fact]
    public async Task VerificationRejectsWrongExpectedValuesInsteadOfAcceptingSuccessfulExecutionAlone()
    {
        var catalog=await Create();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="blob-filter");var project=await Store().LoadAsync(entry.ProjectPath);
        using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(project.ToGraph(),entry.Directory);
        Assert.True(run.Success);Assert.True(ExampleVerifier.Verify(entry.Manifest,run).Passed);
        var wrong=entry.Manifest with { Expected=[new("filter","BlobCount",JsonDefaults.Element(999))] };
        Assert.False(ExampleVerifier.Verify(wrong,run).Passed);
    }
    [Fact]
    public async Task ExplicitTcpUsesActualLoopbackAndOrdinaryProjectKeepsSendSkipped()
    {
        var catalog=await Create();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="tcp-loopback");var project=await Store().LoadAsync(entry.ProjectPath);
        using(var plain=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(project.ToGraph(),entry.Directory))
        {Assert.True(ExampleVerifier.Verify(entry.Manifest,plain).Passed);Assert.Equal(NodeExecutionStatus.Skipped,plain.Nodes["connect"].Status);}
        RunResult? actual=null;
        var received=await LocalTcpExample.RunAsync(async(port,ct)=>
        {
            var enabled=EnableCommunication(project,port);
            actual=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(enabled.ToGraph(),entry.Directory,ct);
        });
        using(actual)
        {
            Assert.NotNull(actual);Assert.True(ExampleVerifier.Verify(entry.Manifest,actual!,communication:true).Passed);
            using var data=JsonDocument.Parse(received);Assert.Equal("OK",data.RootElement.GetProperty("result").GetString());Assert.Equal(2,data.RootElement.GetProperty("count").GetInt32());
        }
    }
    [Fact]
    public async Task CancelledOrFailedLocalTcpCallbackReleasesPortBeforeReturning()
    {
        int port=0;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>LocalTcpExample.RunAsync((p,_)=>{port=p;throw new InvalidOperationException("intentional");}));
        var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();listener.Stop();
        using var cancellation=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>LocalTcpExample.RunAsync(async(p,ct)=>{port=p;cancellation.Cancel();await Task.Delay(10000,ct);},cancellation.Token));
        listener=new TcpListener(IPAddress.Loopback,port);listener.Start();listener.Stop();
    }
    public static ProjectDocument EnableCommunication(ProjectDocument project,int port)=>project with
    {
        Nodes=project.Nodes.Select(n=>n.NodeId switch
        {
            "enable"=>n with{Parameters=JsonDefaults.Element(new{Value=false})},
            "connect"=>n with{Parameters=JsonDefaults.Element(new TcpConnectParameters{Options=new(){Host="127.0.0.1",Port=port}})},
            "send"=>n with{Parameters=JsonDefaults.Element(n.Parameters.Deserialize<TcpSendParameters>(JsonDefaults.Options)! with{SendEnabled=true})},_=>n
        }).ToList()
    };
    private static ProjectDocument WithImage(ProjectDocument project,string image)=>project with
    {Nodes=project.Nodes.Select(n=>n.NodeTypeId=="ImageInput"?n with{Parameters=JsonDefaults.Element(new ImageInputParameters{Path=image})}:n).ToList()};
    public void Dispose()
    {
        var target=Path.GetFullPath(_root);var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!target.StartsWith(temporary,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("VisionStudio-Phase4AExamples-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected fixture path.");
        if(Directory.Exists(target))Directory.Delete(target,true);
    }
}

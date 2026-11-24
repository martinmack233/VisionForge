using System.Text.Json;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase4CIntegrationTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"VisionStudio-Phase4C-Core-"+Guid.NewGuid().ToString("N"));
    private async Task<ExampleCatalog> Catalog()
    {
        await Phase4CExampleFactory.SaveAllAsync(Path.Combine(_root,"Examples"));
        return new(Path.Combine(_root,"Examples"),new ProjectStore(BuiltinRegistration.CreateRegistry(),new Dictionary<string,string>{["cvinspect.pattern"]="0.34.0+0eddfbaa"}));
    }
    [Fact]
    public async Task CameraFramesTraverseOriginalRuntimeAndInvalidFrameNeverReusesOldPose()
    {
        var baseline=FrameHandle.ActiveBytes;var catalog=await Catalog();
        var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="virtual-camera");
        using(var session=new EditorSession())
        {
            await session.OpenAsync(entry.ProjectPath);await session.RunAsync();
            Assert.True(ExampleVerifier.Verify(entry.Manifest.Expected,session.CurrentOutputs,session.Results.ToDictionary(r=>r.NodeId,r=>r.Status)).Passed);
            session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="capture");
            var frameRow=session.SelectedOutputs.Single(o=>o.PortName=="FrameInfo");
            Assert.DoesNotContain("\n",frameRow.Summary);Assert.DoesNotContain("FrameId",frameRow.Summary);
            session.SelectOutput(frameRow);Assert.Contains(session.SelectedOutputInspection!.Fields,f=>f.Name=="采集序号"&&f.Value=="1");
            var first=(TemplateMatchResult)session.CurrentOutputs["match"]["MatchResult"];
            Assert.Same(((ImageFrame)session.CurrentOutputs["capture"]["Image"]).BufferHandle,((ImageFrame)session.CurrentOutputs["input"]["Image"]).BufferHandle);
            await session.RunAsync();var second=(TemplateMatchResult)session.CurrentOutputs["match"]["MatchResult"];
            Assert.Equal(2,((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence);Assert.True(second.Success);
            Assert.True(Math.Abs(first.CenterX-second.CenterX)>20);Assert.Equal(2,session.CurrentOutputs["blob"]["BlobCount"]);
            await session.RunAsync();Assert.Equal(3,((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence);
            Assert.False(((TemplateMatchResult)session.CurrentOutputs["match"]["MatchResult"]).Success);
            Assert.Equal("Skipped",session.Results.Single(r=>r.NodeId=="roi").Status);Assert.Empty(session.CurrentOutputs["roi"]);Assert.Empty(session.CurrentOutputs["blob"]);
            await session.RunAsync();Assert.Equal(4,((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence);Assert.True((bool)session.CurrentOutputs["match"]["Success"]);
            var device=await session.CameraSession.GetVirtualDeviceAsync("virtual-main",new(){FolderPath=Path.Combine(entry.Directory,"images/frames"),FrameRate=5,Loop=true,PixelFormat=PixelFormat.Mono8});
            var revision=session.AcquisitionRevision;await session.NewAsync();Assert.True(session.AcquisitionRevision>revision);Assert.Equal(CameraState.Disposed,device.State);Assert.Empty(session.CurrentOutputs);
            await session.CloseAsync();
        }
        Assert.Equal(baseline,FrameHandle.ActiveBytes);
    }
    [Fact]
    public async Task SaveAsRebasesCameraFolderAndOriginalTemplateReferencesWithoutSchemaChange()
    {
        var catalog=await Catalog();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="virtual-camera");
        using var session=new EditorSession();await session.OpenAsync(entry.ProjectPath);await session.RunAsync();
        var target=Path.Combine(_root,"user-copy/project.json");await session.SaveAsync(target);await session.OpenAsync(target);await session.RunAsync();
        var snapshot=session.SnapshotProject();Assert.Equal(ProjectDocument.CurrentSchemaVersion,snapshot.SchemaVersion);
        var options=snapshot.Nodes.Single(n=>n.NodeTypeId=="VirtualCamera").Parameters.Deserialize<VirtualCameraParameters>(JsonDefaults.Options)!;
        Assert.Equal(Path.GetFullPath(Path.Combine(entry.Directory,"images/frames")),ProjectStore.ResolveResourcePath(Path.GetDirectoryName(target)!,options.FolderPath));
        Assert.Equal(1,((CameraFrameInfo)session.CurrentOutputs["capture"]["FrameInfo"]).Sequence);Assert.True((bool)session.CurrentOutputs["decision"]["IsOk"]);
    }
    [Theory]
    [InlineData("virtual-camera")]
    [InlineData("runtime-host")]
    [InlineData("host-tcp")]
    public async Task CompleteExamplesCopyAndRunWithoutImplicitServerStart(string id)
    {
        var catalog=await Catalog();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id==id);Assert.Empty(await catalog.ValidateAsync(entry));
        var copy=await catalog.CopyAsync(entry,Path.Combine(_root,"copies"));
        using var session=new EditorSession();await session.OpenAsync(copy);await session.RunAsync();
        Assert.True(ExampleVerifier.Verify(entry.Manifest.Expected,session.CurrentOutputs,session.Results.ToDictionary(r=>r.NodeId,r=>r.Status)).Passed);
        Assert.DoesNotContain(session.Nodes,n=>n.TypeId.StartsWith("Tcp",StringComparison.Ordinal));
    }
    [Fact]
    public async Task ConnectedCameraImageDoesNotRequireTheOldFileFallbackDuringEditorOpen()
    {
        var catalog=await Catalog();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="virtual-camera");
        var store=new ProjectStore(BuiltinRegistration.CreateRegistry(),new Dictionary<string,string>{["cvinspect.pattern"]="0.34.0+0eddfbaa"});
        var project=await store.LoadAsync(entry.ProjectPath);
        project=project with{Nodes=project.Nodes.Select(n=>n.NodeId=="input"?n with{Parameters=JsonDefaults.Element(new ImageInputParameters{Path="missing-old-fallback.png"})}:n).ToList()};
        await store.SaveAsync(entry.ProjectPath,project);
        using var session=new EditorSession();await session.OpenAsync(entry.ProjectPath);
        Assert.Null(session.DisplayImage);await session.RunAsync();Assert.True((bool)session.CurrentOutputs["decision"]["IsOk"]);
        await session.SaveAsync();
    }
    [Fact]
    public async Task AnUnlistedCameraFrameFailsExampleIntegrityInsteadOfChangingSequenceSilently()
    {
        var catalog=await Catalog();var entry=(await catalog.LoadAsync()).Single(e=>e.Manifest.Id=="virtual-camera");
        File.Copy(Path.Combine(entry.Directory,"images/frames/01-target.png"),Path.Combine(entry.Directory,"images/frames/00-unlisted.png"));
        Assert.Contains(await catalog.ValidateAsync(entry),e=>e.Contains("未列入清单",StringComparison.Ordinal));
    }
    public void Dispose()
    {
        if(Directory.Exists(_root)&&Path.GetFileName(_root).StartsWith("VisionStudio-Phase4C-Core-",StringComparison.Ordinal))Directory.Delete(_root,true);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;

/// <summary>File-camera and headless examples use the same Schema1 and Runtime as the editor.</summary>
public static class Phase4CExampleFactory
{
    public static async Task SaveAllAsync(string directory, CancellationToken ct = default)
    {
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry(), new Dictionary<string,string> { ["cvinspect.pattern"]="0.34.0+0eddfbaa" });
        foreach (var id in new[] { "virtual-camera", "runtime-host", "host-tcp" })
        {
            var folder = Path.Combine(Path.GetFullPath(directory), id); Directory.CreateDirectory(Path.Combine(folder, "images"));
            ProjectDocument project; ExampleManifest manifest;
            if (id == "virtual-camera")
            {
                DemoProjectFactory.CreateResources(Path.Combine(folder, "images"));
                var frames = Path.Combine(folder, "images/frames"); Directory.CreateDirectory(frames);
                File.Copy(Path.Combine(folder,"images/sample.png"),Path.Combine(frames,"01-target.png"),true);
                File.Copy(Path.Combine(folder,"images/shifted.png"),Path.Combine(frames,"02-shifted.png"),true);
                File.Copy(Path.Combine(folder,"images/absent.png"),Path.Combine(frames,"03-absent.png"),true);
                var original = DemoProjectFactory.Create(false);
                project = original with
                {
                    ProjectId = StableId(id), ProjectName = "虚拟相机定位检测",
                    Nodes = original.Nodes.Select(n => n.NodeTypeId switch
                    {
                        "ImageInput" => n with { Parameters=JsonDefaults.Element(new ImageInputParameters { Path="images/frames/01-target.png" }) },
                        "TemplateMatching" => n with { Parameters=JsonDefaults.Element(n.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)! with { TemplatePath="images/template.json" }) },
                        _=>n
                    }).Prepend(N("capture","VirtualCamera",new VirtualCameraParameters { CameraId="virtual-main",FolderPath="images/frames",FrameRate=5,Loop=true,PixelFormat=PixelFormat.Mono8 })).ToList(),
                    Connections = original.Connections.Prepend(new NodeConnection("capture","Image","input","Image")).ToList(),
                    TemplateResources = [new("locator","images/template.json")]
                };
                manifest = new() { Id=id,Name="虚拟相机定位检测",Category="图像采集",Tags=["VirtualCamera","模板匹配","ROI","第四阶段C"],
                    Description="从本地文件夹采集真实帧，经原模板定位、ROI跟随、Blob检测和OK/NG。",
                    Instructions="打开或一键运行。首次图像为+10度，下一次为平移/-10度，再下一次无目标；循环回到首帧。第三帧应匹配失败并跳过ROI/Blob，不沿用旧姿态。设备页可从当前节点同步配置，预览与运行分开；运行前停止预览，节点采集下一帧。",
                    Verification="新会话首帧FrameInfo.Sequence=1、模板成功、BlobCount=2、IsOk=True；第二帧定位应改变；第三帧Match.Success=False且ROI/Blob Skipped。所有坐标来自原图，设备无真实曝光/增益能力。",
                    Expected=[C("capture","FrameInfo",1,property:"Sequence"),C("match","Success",true),C("blob","BlobCount",2),C("decision","IsOk",true)] };
            }
            else
            {
                using (var image = new Mat(120,160,MatType.CV_8UC1,Scalar.All(0)))
                {
                    Cv2.Rectangle(image,new Rect(20,20,20,20),Scalar.All(255),-1); Cv2.Rectangle(image,new Rect(90,60,25,25),Scalar.All(255),-1);
                    Cv2.ImWrite(Path.Combine(folder,"images/input.png"),image);
                }
                project = new() { ProjectId=StableId(id),ProjectName=id=="runtime-host"?"RuntimeHost独立检测":"上位机TCP调用检测",
                    Nodes=[N("input","ImageInput",new{Path="images/input.png"}),N("blob","BlobDetection",new{MinArea=80}),N("decision","ResultDecision",new{MinimumBlobCount=2,MaximumBlobCount=2}),N("report","ResultDisplay",new{})],
                    Connections=[new("input","Image","blob","Image"),new("blob","BlobCount","decision","BlobCount"),new("decision","Decision","report","Decision"),new("blob","BlobCount","report","BlobCount")] };
                manifest = new() { Id=id,Name=project.ProjectName,Category="独立运行",Tags=["RuntimeHost","TCP","第四阶段C"],
                    Description=id=="runtime-host"?"同一工程通过独立RuntimeHost执行，无需启动WPF。":"显式启动Loopback Host，由版本化长度前缀TCP客户端加载、运行、轮询并取回真实结果。",
                    Instructions="一键运行仅执行离线检测图，不自动启动监听。独立模式：VisionPlatform.RuntimeHost.exe --project Examples/"+id+"/project.json --once。TCP模式：启动VisionPlatform.RuntimeHost.exe --port 0，记录输出地址与端口，再使用python tools/runtimehost_client.py --port 实际端口 --project 本工程绝对路径加载并运行。完整参数和消息见Host协议文档。",
                    Verification="真实BlobCount=2、IsOk=True；Host结果必须含当前RunId、引擎RunId与节点状态/耗时；取消/断线后资源应释放。结果仅包含图像元数据，不传递原生指针。",
                    ExternalDependencies=["TCP演示需用户显式启动本包RuntimeHost和测试客户端；默认仅Loopback，一键运行不创建服务器。"],
                    Expected=[C("blob","BlobCount",2),C("report","IsOk",true)] };
            }
            await store.SaveAsync(Path.Combine(folder,"project.json"),project,ct);
            var source = id=="virtual-camera"?"images/frames/01-target.png":"images/input.png";
            using(var image=Cv2.ImRead(Path.Combine(folder,source),ImreadModes.Color))
            using(var preview=new Mat()) { Cv2.Resize(image,preview,new Size(240,160),0,0,InterpolationFlags.Area);Cv2.ImWrite(Path.Combine(folder,"preview.png"),preview); }
            await File.WriteAllTextAsync(Path.Combine(folder,"README.md"),$"# {manifest.Name}\n\n{manifest.Description}\n\n{manifest.Instructions}\n\n{manifest.Verification}\n\n全部图像为自制合成资源，文件相机通过通用相机接口工作；未验证真实硬件或工业精度。内置示例只读，修改先创建副本。\n",ct);
            foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories).Where(f=>Path.GetFileName(f)!="manifest.json"))
                manifest.Files[Path.GetRelativePath(folder,file).Replace('\\','/')]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            await File.WriteAllTextAsync(Path.Combine(folder,"manifest.json"),JsonSerializer.Serialize(manifest,JsonDefaults.Options),ct);
        }
    }
    private static Guid StableId(string id)=>new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("VisionStudio-Phase4C-"+id))[..16]);
    private static NodeSpecification N(string id,string type,object p)=>new(id,type,type,JsonDefaults.Element(p));
    private static ExampleCheck C(string node,string port,object value,double tolerance=0,string property="")=>new(node,port,JsonDefaults.Element(value),tolerance,property);
}

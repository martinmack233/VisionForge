using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;

public sealed record ExampleCheck(string NodeId, string Port, JsonElement Expected, double Tolerance = 0, string Property = "");
public sealed record ExampleCase(string Id, string Name, string ImagePath, IReadOnlyList<ExampleCheck> Checks, bool ExpectedRunSuccess = true);
public sealed record ExampleManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string Difficulty { get; init; } = "基础";
    public string[] Tags { get; init; } = [];
    public string Project { get; init; } = "project.json";
    public string Preview { get; init; } = "preview.png";
    public string Instructions { get; init; } = "";
    public string Flow { get; init; } = "";
    public string LearningGoals { get; init; } = "";
    public string TryIt { get; init; } = "";
    public string ExpectedSummary { get; init; } = "";
    public string Verification { get; init; } = "";
    public string[] ExternalDependencies { get; init; } = [];
    public bool RequiresExplicitCommunication { get; init; }
    public IReadOnlyList<ExampleCheck> Expected { get; init; } = [];
    public IReadOnlyList<ExampleCheck> CommunicationExpected { get; init; } = [];
    public IReadOnlyList<ExampleCase> Cases { get; init; } = [];
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Offline synthetic examples. Expected values are specified from fixture geometry, never copied from a run.</summary>
public static class Phase4AExampleFactory
{
    private static NodeSpecification N(string id, string type, object p) => new(id, type, type, JsonDefaults.Element(p));
    private static NodeConnection E(string a, string b, string c, string d) => new(a, b, c, d);
    private static ExampleCheck C(string node, string port, object value, double tolerance = 0, string property = "") => new(node, port, JsonDefaults.Element(value), tolerance, property);

    public static async Task SaveAllAsync(string directory, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var store = new ProjectStore(BuiltinRegistration.CreateRegistry(), new Dictionary<string,string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
        foreach (var id in new[] { "basic", "blob-filter", "template-follow", "rotated-crop", "logic", "tcp-loopback", "caliper", "circle" })
        {
            ct.ThrowIfCancellationRequested(); var folder = Path.Combine(root, id); Directory.CreateDirectory(Path.Combine(folder, "images"));
            var (project, manifest) = await Task.Run(() => Create(id, folder), ct).ConfigureAwait(false);
            await store.SaveAsync(Path.Combine(folder, "project.json"), project, ct).ConfigureAwait(false);
            var previewSource = project.Nodes.First(n=>n.NodeTypeId=="ImageInput").Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!.Path;
            using (var source = Cv2.ImRead(Path.Combine(folder, previewSource), ImreadModes.Color))
            using (var preview = new Mat())
            {
                var scale = Math.Min(240d / source.Width, 160d / source.Height);
                Cv2.Resize(source, preview, new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))), 0, 0, InterpolationFlags.Area);
                using var cover = new Mat(160, 240, MatType.CV_8UC3, Scalar.All(245));
                using var target = new Mat(cover, new Rect((240 - preview.Width) / 2, (160 - preview.Height) / 2, preview.Width, preview.Height));
                preview.CopyTo(target); Cv2.ImWrite(Path.Combine(folder, "preview.png"), cover);
            }
            File.WriteAllText(Path.Combine(folder, "README.md"), $"# {manifest.Name}\n\n{manifest.Description}\n\n{manifest.Instructions}\n\n验证：{manifest.Verification}\n\n全部图像为确定性合成图，测量单位为像素，不代表工业标定精度。通过示例中心先创建副本，内置目录只作模板。\n");
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)))
                manifest.Files[Path.GetRelativePath(folder, file).Replace('\\','/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options), ct).ConfigureAwait(false);
        }
    }

    private static (ProjectDocument Project, ExampleManifest Manifest) Create(string id, string folder)
    {
        var input = N("input", "ImageInput", new { Path = "images/input.png" });
        var checks = new List<ExampleCheck>(); var cases = new List<ExampleCase>();
        var nodes = new List<NodeSpecification> { input }; var edges = new List<NodeConnection>();
        var name = ""; var category = ""; var description = ""; var instructions = "选择打开查看完整流程，或一键运行；修改参数后应用并重新运行。";
        var project = new ProjectDocument();
        if (id is "basic" or "blob-filter" or "logic" or "tcp-loopback")
        {
            WriteBlobs(Path.Combine(folder, "images/input.png"), 2); WriteBlobs(Path.Combine(folder, "images/abnormal.png"), 3);
        }
        switch (id)
        {
            case "basic":
                name = "基础图像处理"; category = "图像处理"; description = "高斯滤波、固定阈值和图像预览。可调整ThresholdValue观察边缘变化。";
                nodes.AddRange([N("blur","GaussianBlur",new { Options = new { KernelSize=5,SigmaX=1.2 } }), N("threshold","Threshold",new { ThresholdValue=128 }), N("display","ImageDisplay",new { })]);
                edges.AddRange([E("input","Image","blur","Image"), E("blur","ProcessedImage","threshold","Image"), E("threshold","ProcessedImage","display","Image")]);
                checks.AddRange([C("display","Image",160,property:"Width"),C("display","Image",120,property:"Height"),
                    C("display","Image",255,property:"Pixel[25,25]"),C("display","Image",0,property:"Pixel[0,0]"),C("threshold","$status","Success")]); break;
            case "blob-filter":
                name = "Blob 缺陷筛选"; category = "目标检测"; description = "正常图有2个大目标，异常图有3个；形态学去除微小噪点，面积筛选后得到真实OK/NG。";
                nodes.AddRange([N("threshold","Threshold",new { ThresholdValue=128 }),N("morph","Morphology",new { Operation="Open",KernelWidth=3,KernelHeight=3 }),
                    N("blob","BlobDetection",new { MinArea=1 }),N("filter","BlobFilter",new { Area=new { Enabled=true,Minimum=80,Maximum=1000 } }),N("decision","ResultDecision",new { MinimumBlobCount=2,MaximumBlobCount=2 }),N("report","ResultDisplay",new { })]);
                edges.AddRange([E("input","Image","threshold","Image"),E("threshold","ProcessedImage","morph","Image"),E("morph","ProcessedImage","blob","Image"),E("blob","BlobResults","filter","BlobResults"),E("filter","BlobCount","decision","BlobCount"),E("decision","Decision","report","Decision"),E("filter","BlobCount","report","BlobCount")]);
                checks.AddRange([C("filter","BlobCount",2),C("report","IsOk",true)]);
                cases.Add(new("normal","正常：2个目标","images/input.png",checks.ToArray())); cases.Add(new("abnormal","异常：3个目标","images/abnormal.png",[C("filter","BlobCount",3),C("report","IsOk",false)])); break;
            case "template-follow":
            case "rotated-crop":
                DemoProjectFactory.CreateResources(Path.Combine(folder,"images"));
                var original = DemoProjectFactory.Create(false);
                var match = original.Nodes.Single(n=>n.NodeId=="match") with { Parameters = JsonDefaults.Element(new TemplateMatchingParameters { TemplatePath="images/template.json",Options=original.Nodes.Single(n=>n.NodeId=="match").Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!.Options }) };
                nodes = [N("input","ImageInput",new { Path="images/sample.png" }),match];
                project = original with { TemplateResources=[new("locator","images/template.json")] };
                if(id=="template-follow")
                {
                    name="模板匹配与 ROI 跟随";category="模板匹配";description="两张平移/旋转图验证定位真正改变Blob搜索区域，失配图验证旧姿态失效。";
                    nodes.AddRange(original.Nodes.Where(n=>n.NodeId is "roi" or "blob" or "decision"));
                    edges = original.Connections.ToList(); nodes.Add(N("report","ResultDisplay",new { }));
                    edges.AddRange([E("decision","Decision","report","Decision"),E("blob","BlobCount","report","BlobCount"),E("match","MatchResult","report","MatchResult")]);
                    checks.AddRange([C("match","Success",true),C("blob","BlobCount",2),C("report","IsOk",true)]);
                    cases.Add(new("original","定位+10度","images/sample.png",checks.ToArray()));
                    cases.Add(new("shifted","平移和-10度","images/shifted.png",checks.ToArray()));
                    cases.Add(new("absent","失配：不沿用旧ROI","images/absent.png",[C("match","Success",false),C("roi","$status","Skipped"),C("blob","$status","Skipped")],false));
                }
                else
                {
                    name="旋转裁切";category="图像变换";description="模板定位倾斜目标，输出标准方向裁切图及从裁切图到原图的坐标变换。";
                    // Existing RotatedCrop deliberately multiplies crop dimensions by matched Scale.
                    // Use a known unit-scale rotated scene for this fixed 72x64 teaching example.
                    WriteUnitScaleScene(folder);
                    nodes[0]=N("input","ImageInput",new { Path="images/unit-scale.png" });
                    nodes.AddRange([N("crop","RotatedCrop",new { Width=72,Height=64 }),N("display","ImageDisplay",new { })]);
                    edges = [E("input","Image","match","Image"),E("input","Image","crop","Image"),E("match","Pose2D","crop","Pose2D"),E("crop","CroppedImage","display","Image")];
                    checks.AddRange([C("match","Success",true),C("display","Image",72,property:"Width"),C("display","Image",64,property:"Height")]);
                    project = project with { ROI=[] };
                }
                break;
            case "logic":
                name="条件分支判断";category="逻辑控制";description="根据真实Blob数量选择True/False分支；未选择的分支在调用前即Skipped。";
                nodes.AddRange([N("threshold","Threshold",new { ThresholdValue=128 }),N("blob","BlobDetection",new { MinArea=80 }),N("compare","Compare",new { ValueB=2,Operator="Equal" }),N("if","IfElse",new { }),N("true","ResultDisplay",new { }),N("false","ResultDisplay",new { })]);
                edges.AddRange([E("input","Image","threshold","Image"),E("threshold","ProcessedImage","blob","Image"),E("blob","BlobCount","compare","ValueA"),E("compare","Result","if","Condition"),E("compare","Result","true","IsOk"),E("compare","Result","false","IsOk"),E("if","True","true",NodePorts.Execute),E("if","False","false",NodePorts.Execute),E("blob","BlobCount","true","BlobCount"),E("blob","BlobCount","false","BlobCount")]);
                checks.AddRange([C("compare","Result",true),C("true","$status","Success"),C("false","$status","Skipped")]);
                cases.Add(new("true","True：2个目标","images/input.png",checks.ToArray()));cases.Add(new("false","False：3个目标","images/abnormal.png",[C("compare","Result",false),C("true","$status","Skipped"),C("false","$status","Success")])); break;
            case "tcp-loopback":
                name="TCP 本地通信";category="通信";description="显式启动一次本机回环测试，检测结果经真实TCP字节流发送并与接收值核对；普通打开不监听、不连接、不发送。";
                instructions="点击“启动本地 TCP 测试”，由程序建立临时Loopback接收服务；普通运行的gate默认关闭。勿将此教学发送标识用于生产业务。";
                nodes.AddRange([N("blob","BlobDetection",new { MinArea=80 }),N("decision","ResultDecision",new { MinimumBlobCount=2,MaximumBlobCount=2 }),N("report","ResultDisplay",new { }),
                    N("enable","LogicNot",new { Value=true }),N("gate","IfElse",new { }),N("connect","TcpConnect",new { Options=new { Host="127.0.0.1",Port=9000 } }),
                    N("send","TcpSend",new { SendEnabled=false,DeliveryKey="example-local-1" }),N("close","TcpDisconnect",new { })]);
                edges.AddRange([E("input","Image","blob","Image"),E("blob","BlobCount","decision","BlobCount"),E("decision","Decision","report","Decision"),E("blob","BlobCount","report","BlobCount"),E("enable","Result","gate","Condition"),E("gate","True","connect",NodePorts.Execute),E("connect","Connection","send","Connection"),E("report","Result","send","Result"),E("connect","Connection","close","Connection"),E("send",NodePorts.Completed,"close",NodePorts.Execute)]);
                checks.AddRange([C("report","IsOk",true),C("connect","$status","Skipped"),C("send","$status","Skipped")]); break;
            case "caliper":
                name="卡尺亚像素边缘测量";category="几何测量";description="投影灰度剖面、梯度峰值抛物线拟合定位非整数边缘，再最小二乘拟合直线；输出像素坐标。";
                WriteEdge(Path.Combine(folder,"images/input.png"));
                nodes.AddRange([N("caliper","Caliper",new { CenterX=120.25,CenterY=80,SearchDirectionDegrees=0,SearchLength=40,ProjectionWidth=5,Span=100,CaliperCount=16,Polarity="DarkToLight" }),N("fit","LineFit",new { }),N("report","ResultDisplay",new { })]);
                edges.AddRange([E("input","Image","caliper","Image"),E("caliper","Points","fit","Points"),E("caliper","Result","report","Measurement")]);
                checks.AddRange([C("caliper","Valid",true),C("caliper","Result",120.25,.3,"Values.MeanX"),C("fit","Valid",true)]);break;
            case "circle":
                name="圆形尺寸测量";category="几何测量";description="沿预设圆周使用径向卡尺提取真实边缘点，再抗离群圆拟合，显示圆心、半径、直径和误差。";
                WriteCircle(Path.Combine(folder,"images/input.png"));
                nodes.AddRange([N("edges","CircleEdgePoints",new { CenterX=128,CenterY=128,Radius=60,SearchLength=24,ProjectionWidth=3,SampleCount=64,Polarity="LightToDark" }),N("circle","CircleFit",new { RejectOutliers=true,OutlierThreshold=1.5 }),N("report","ResultDisplay",new { })]);
                edges.AddRange([E("input","Image","edges","Image"),E("edges","Points","circle","Points"),E("circle","Result","report","Measurement")]);
                checks.AddRange([C("circle","Valid",true),C("circle","Radius",60,.35),C("circle","Result",128,.2,"Values.CenterX"),C("circle","Result",128,.2,"Values.CenterY")]);break;
        }
        var result = project with { ProjectName=name,Nodes=nodes,Connections=edges };
        var manifest = new ExampleManifest { Id=id,Name=name,Category=category,Description=description,Instructions=instructions,
            Difficulty=id is "basic" or "blob-filter" ? "基础" : "进阶",Tags=[category,"离线","合成图"],Expected=checks,
            Verification="运行当前工程，逐节点核对manifest.json中固定预期值及容差；验证状态来自本次Runtime输出。",Cases=cases,
            RequiresExplicitCommunication=id=="tcp-loopback",CommunicationExpected=id=="tcp-loopback" ? [C("send","Success",true),C("close","Success",true)] : [] };
        return(result,manifest);
    }
    private static void WriteBlobs(string path,int count)
    {
        using var image = new Mat(120,160,MatType.CV_8UC1,Scalar.All(20));
        foreach(var r in new[]{new Rect(20,20,18,18),new Rect(75,30,20,16),new Rect(110,80,16,16)}.Take(count))Cv2.Rectangle(image,r,Scalar.All(230),-1);
        Cv2.Rectangle(image,new Rect(5,100,2,2),Scalar.All(245),-1);Cv2.ImWrite(path,image);
    }
    private static void WriteEdge(string path)
    {
        using var image=new Mat(160,256,MatType.CV_8UC1);
        for(var y=0;y<160;y++)for(var x=0;x<256;x++)image.Set(y,x,(byte)Math.Clamp(Math.Round(20+210*(1+Math.Tanh((x-120.25)/1.2))/2),0,255));
        Cv2.ImWrite(path,image);
    }
    private static void WriteCircle(string path)
    {
        using var image=new Mat(256,256,MatType.CV_8UC1);
        for(var y=0;y<256;y++)for(var x=0;x<256;x++){var d=Math.Sqrt((x-128d)*(x-128d)+(y-128d)*(y-128d));image.Set(y,x,(byte)Math.Clamp(Math.Round(20+210*(1-Math.Tanh((d-60)/1.2))/2),0,255));}
        Cv2.ImWrite(path,image);
    }
    private static void WriteUnitScaleScene(string folder)
    {
        using var template=TemplateResourceStore.Load(Path.Combine(folder,"images/template.json"));
        using var source=new OpenCvImageLease(template.Image);
        var r=10*Math.PI/180;var c=Math.Cos(r);var s=Math.Sin(r);var tx=(template.Image.Width-1)/2d;var ty=(template.Image.Height-1)/2d;
        using var map=new Mat(2,3,MatType.CV_64FC1);
        map.Set(0,0,c);map.Set(0,1,-s);map.Set(0,2,221.5-c*tx+s*ty);
        map.Set(1,0,s);map.Set(1,1,c);map.Set(1,2,163.5-s*tx-c*ty);
        using var scene=new Mat();Cv2.WarpAffine(source.Mat,scene,map,new Size(384,320),InterpolationFlags.Linear,BorderTypes.Constant,Scalar.All(35));
        Cv2.ImWrite(Path.Combine(folder,"images/unit-scale.png"),scene);
    }
}

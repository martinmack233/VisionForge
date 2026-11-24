using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;
/// <summary>Small self-authored raster fixtures; no alignment, external model or device operations.</summary>
public static class InspectionExampleFactory
{
    public static readonly string[] Ids=["inspection-aligned-difference","inspection-color-shapes","inspection-hough-measure"];
    public static SubflowDefinition GeometryMeasure()=>new()
    {
        Nodes=[CollectionSubflows.N("current","IterationInput",new{}),CollectionSubflows.N("geometry","ItemGeometry",new{}),CollectionSubflows.N("measure","DistanceMeasure",new{}),CollectionSubflows.N("judge","Compare",new{ValueB=50d,Operator="GreaterOrEqual"}),CollectionSubflows.N("collect","IterationOutput",new{})],
        Connections=[new("current","Item","geometry","Item"),new("geometry","PointA","measure","PointA"),new("geometry","PointB","measure","PointB"),new("measure","Result","collect","Measurement"),new("measure","Distance","collect","Number"),new("measure","Distance","judge","ValueA"),new("judge","Result","collect","IsOk")],
        NodeLocations=new(){["current"]=new(20,60),["geometry"]=new(380,60),["measure"]=new(740,60),["judge"]=new(1100,60),["collect"]=new(1460,60)}
    };
    public static ProjectDocument Create(int index)
    {
        NodeSpecification N(string id,string type,object p)=>CollectionSubflows.N(id,type,p);
        var nodes=new List<NodeSpecification>{N("input","ImageInput",new ImageInputParameters{Path="input.png"})};var edges=new List<NodeConnection>();
        void E(string a,string output,string b,string input)=>edges.Add(new(a,output,b,input));
        if(index==0)
        {
            nodes.AddRange([N("reference","ImageInput",new ImageInputParameters{Path="reference.png"}),N("difference","ImageArithmetic",new ImageArithmeticOptions{AllowDifferentFrames=true}),N("threshold","Threshold",new ThresholdOptions{ThresholdValue=25}),N("contours","FindContours",new FindContoursOptions{Retrieval=ContourRetrieval.External}),N("shape","ContourAnalysis",new ShapeAnalysisOptions()),N("filter","CollectionFilter",new CollectionFilterOptions{Field="Area",Number=100}),N("count","CollectionCount",new{}),N("judge","Compare",new{ValueB=0d,Operator="Equal"}),N("result","ResultDisplay",new{}),N("statistics","RegionStatistics",new{})]);
            E("input","Image","difference","ImageA");E("reference","Image","difference","ImageB");E("difference","ProcessedImage","threshold","Image");E("difference","ProcessedImage","statistics","Image");E("threshold","ProcessedImage","contours","Image");E("contours","Collection","shape","Collection");E("shape","Collection","filter","Collection");E("filter","Collection","count","Collection");E("count","Count","judge","ValueA");E("judge","Result","result","IsOk");E("count","Count","result","BlobCount");
        }
        else if(index==1)
        {
            nodes.AddRange([N("color","ColorSpaceConvert",new ColorConversionOptions()),N("range","ColorInRange",new ColorRangeOptions{Lower0=40,Upper0=80,Lower1=150,Lower2=100}),N("morphology","Morphology",new MorphologyOptions{Operation=MorphologyOperation.Open,KernelWidth=3,KernelHeight=3}),N("contours","FindContours",new FindContoursOptions{Retrieval=ContourRetrieval.Tree,Chain=ContourChain.None}),N("shape","ContourAnalysis",new ShapeAnalysisOptions()),N("filter","CollectionFilter",new CollectionFilterOptions{Field="Area",Number=100})]);
            E("input","Image","color","Image");E("color","ColorData","range","ColorData");E("range","BinaryImage","morphology","Image");E("morphology","ProcessedImage","contours","Image");E("contours","Collection","shape","Collection");E("shape","Collection","filter","Collection");
        }
        else if(index==2)
        {
            nodes.AddRange([N("edges","Canny",new CannyOptions()),N("hough","HoughLinesP",new HoughLinesOptions{Threshold=45,MinimumLength=70}),N("filter","CollectionFilter",new CollectionFilterOptions{Field="Length",Number=80}),N("sort","CollectionSort",new{Field="Length",Descending=true}),N("each","ForEach",new ForEachOptions{Body=GeometryMeasure(),MaximumItems=100,DebugIterations=4}),N("circles","HoughCircles",new HoughCirclesOptions{MinimumRadius=40,MaximumRadius=55,MinimumDistance=100,CenterThreshold=24})]);
            E("input","Image","edges","Image");E("input","Image","circles","Image");E("edges","Edges","hough","Image");E("hough","Collection","filter","Collection");E("filter","Collection","sort","Collection");E("sort","Collection","each","Collection");
        }
        else throw new ArgumentOutOfRangeException(nameof(index));
        nodes.Add(N("display","CollectionDisplay",new CollectionDisplayOptions{Fields=index==2?"Index,Number,State":"Index,Area,Perimeter,Rectangularity",SummaryLines=4,MaximumOverlays=50}));
        E(index==2?"each":"filter",index==2?"Results":"Collection","display","Collection");E("input","Image","display","Image");
        return new(){ProjectName=new[]{"已对齐图像 · 差分缺陷检测","颜色区域 · HSV与全部轮廓","多几何目标 · 霍夫与逐项测量"}[index],Nodes=nodes,Connections=edges};
    }
    public static async Task SaveAllAsync(string root,CancellationToken ct=default)
    {
        var store=new ProjectStore(BuiltinRegistration.CreateRegistry());
        for(var index=0;index<Ids.Length;index++)
        {
            var folder=Path.Combine(Path.GetFullPath(root),Ids[index]);if(File.Exists(Path.Combine(folder,"project.json")))throw new IOException("示例已存在，拒绝覆盖："+folder);Directory.CreateDirectory(folder);
            using var image=new Mat(480,640,index==1?MatType.CV_8UC3:MatType.CV_8UC1,Scalar.All(index==0?80:0));
            if(index==0)
            {
                Cv2.Rectangle(image,new Rect(70,70,460,300),Scalar.All(120),-1);Cv2.Circle(image,new(180,180),50,Scalar.All(160),-1);Cv2.ImWrite(Path.Combine(folder,"reference.png"),image);
                Cv2.Rectangle(image,new Rect(300,150,40,25),Scalar.All(240),-1);Cv2.Circle(image,new(420,300),18,Scalar.All(15),-1);
            }
            else if(index==1){Cv2.Rectangle(image,new Rect(65,75,140,100),new Scalar(0,220,0),-1);Cv2.Ellipse(image,new RotatedRect(new(420,290),new(170,90),25),new Scalar(0,255,0),-1);Cv2.Circle(image,new(400,100),45,new Scalar(0,0,255),-1);Cv2.Rectangle(image,new Rect(80,310,100,80),new Scalar(255,0,0),-1);}
            else{Cv2.Line(image,new(45,70),new(550,70),Scalar.All(255),2);Cv2.Line(image,new(80,400),new(380,220),Scalar.All(255),2);Cv2.Circle(image,new(480,300),48,Scalar.All(255),2);}
            Cv2.ImWrite(Path.Combine(folder,"input.png"),image);using var preview=new Mat();Cv2.Resize(image,preview,new Size(400,300));Cv2.ImWrite(Path.Combine(folder,"preview.png"),preview);
            var project=Create(index);await store.SaveAsync(Path.Combine(folder,"project.json"),project,ct);
            var instructions=new[]{"先复制为用户工程，再运行。两幅图由同一坐标网格合成，已经对齐；本例不执行自动配准。显式允许参考图跨帧，尺寸/格式/坐标仍须完全相同。应找到2个差异目标，业务判定NG，节点执行成功。可查看原始差分与真实区域统计。","复制并运行。HSV H=0..179，S/V=0..255；绿色范围得到2个区域，红/蓝区域不通过。工作颜色数据不当作BGR显示，Preview是显式还原的显示图。轮廓分析每个目标，保留旋转角度及来源。","复制并运行。Canny真实边缘→概率霍夫线段→长度筛选/排序→顺序子流程使用原DistanceMeasure测量端点距离。另有独立霍夫圆输出口。算法不是亚像素拟合。线段数受OpenCV边缘/投票影响，不把重复边缘线段当物理目标去重。"}[index];
            await File.WriteAllTextAsync(Path.Combine(folder,"README.md"),$"# {project.ProjectName}\n\n{instructions}\n\n所有图片为本平台OpenCV绘制的自制合成资源，不使用业务数据，不依赖YOLO模型，不代表产线准确率。图像坐标x向右/y向下；角度顺时针，最小矩形宽>=高，角度[-90,90)。轮廓面积是边界多边形面积，孔洞单独保留，不等于前景像素数；不是自动扣孔的净面积。\n\n在方案示例中复制后运行；集合面板查看完整结果、选中高亮及局部图像。逐项入口编辑现有子流程；不允许嵌套或副作用节点。工程资源均相对路径。可通过工程包导出/预检/导入到新目录验证重定位。\n",ct);
            var manifest=new ExampleManifest{Id=Ids[index],Name=project.ProjectName,Category="传统视觉检测",Tags=["自制合成","集合",index==0?"已对齐差分":index==1?"HSV/Lab":"霍夫几何"],Description=instructions,Instructions=instructions,Verification=index==0?"filter.Collection数量2；result.IsOk=false；流程执行成功。":index==1?"绿色轮廓2项，每项具有真实面积/周长/旋转矩形。":"非空真实线段集合，逐项Number与端点距离一致，圆集合非空。",Expected=index==0?[new("count","Count",JsonDefaults.Element(2)),new("result","IsOk",JsonDefaults.Element(false))]:[]};
            foreach(var file in Directory.EnumerateFiles(folder))manifest.Files[Path.GetFileName(file)]=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file,ct)));
            await File.WriteAllTextAsync(Path.Combine(folder,"manifest.json"),JsonSerializer.Serialize(manifest,JsonDefaults.Options),ct);
        }
    }
}

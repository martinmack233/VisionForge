using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Algorithms.Utilities;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Samples;
/// <summary>Small self-authored fixtures; run through the existing Runtime, never cameras or communication.</summary>
public static class OperatorExpansionSamples
{
    public static readonly string[] Ids=["operators-contrast-edges","operators-matrix-warp","operators-random-logic","operators-file-read"];
    private static NodeSpecification N(string id,string type,object options)=>new(id,type,type,JsonDefaults.Element(options));
    private static NodeConnection E(string from,string output,string to,string input)=>new(from,output,to,input);
    private static ExampleCheck C(string node,string port,object expected,double tolerance=0,string property="")=>new(node,port,JsonDefaults.Element(expected),tolerance,property);
    public static ProjectDocument Create(int index)
    {
        var nodes=new List<NodeSpecification>();var connections=new List<NodeConnection>();
        if(index==0)
        {
            nodes.AddRange([N("input","ImageInput",new ImageInputParameters{Path="input.png"}),N("linear","LinearContrast",new LinearContrastOptions{Alpha=2,Beta=-220}),N("stretch","RangeStretch",new RangeStretchOptions{InputMinimum=0,InputMaximum=40}),N("canny","Canny",new CannyOptions{Low=20,High=40}),N("contours","FindContours",new FindContoursOptions{Retrieval=ContourRetrieval.External}),N("shape","ContourAnalysis",new ShapeAnalysisOptions()),N("count","CollectionCount",new CollectionCountOptions()),N("gradient_x","Sobel",new SobelOptions{Dx=1,Dy=0}),N("gradient_y","Sobel",new SobelOptions{Dx=0,Dy=1}),N("gradient","GradientMagnitudeDirection",new GradientMagnitudeDirectionOptions()),N("raw_display","ImageDisplay",new ImageDisplayParameters()),N("display","CollectionDisplay",new CollectionDisplayOptions{Fields="Index,Area,Perimeter,Closed",MaximumOverlays=20})]);
            connections.AddRange([E("input","Image","linear","Image"),E("input","Image","raw_display","Image"),E("linear","ProcessedImage","stretch","Image"),E("stretch","ProcessedImage","canny","Image"),E("canny","Edges","contours","Image"),E("contours","Collection","shape","Collection"),E("shape","Collection","count","Collection"),E("shape","Collection","display","Collection"),E("stretch","ProcessedImage","display","Image"),E("stretch","ProcessedImage","gradient_x","Image"),E("stretch","ProcessedImage","gradient_y","Image"),E("gradient_x","Derivative","gradient","GradientX"),E("gradient_y","Derivative","gradient","GradientY")]);
        }
        else if(index==1)
        {
            nodes.AddRange([N("input","ImageInput",new ImageInputParameters{Path="input.png"}),N("affine","TransformCreate",new TransformCreateOptions{MatrixText="1,0.15,20;-0.05,1,15",TargetSpaceId="affine-demo"}),N("affine_image","WarpImage",new WarpImageOptions{Width=320,Height=240}),N("perspective","TransformCreate",new TransformCreateOptions{Kind=GeometricTransformKind.Perspective,MatrixText="1,0.04,8;0.02,1,5;0.0008,0.0003,1",TargetSpaceId="perspective-demo"}),N("perspective_image","WarpImage",new WarpImageOptions{Width=320,Height=240}),N("reference_roi","RoiCollection",new RoiCollectionOptions{Regions=[new(80,80,60,40)]}),N("mapped_roi","MapGeometry",new MapGeometryOptions()),N("mapped_point","MapGeometry",new MapGeometryOptions{X=80,Y=80}),N("estimate","EstimateTransform",new EstimateTransformOptions{SourcePointsText="0,0;100,0;0,100;100,100",TargetPointsText="20,15;120,10;35,115;135,110",TargetSpaceId="estimated-demo"}),N("matrix","MatrixCreate",new MatrixCreateOptions{ValuesText="4,7;2,6"}),N("inverse","MatrixMath",new MatrixMathOptions{Operation=MatrixOperation.Inverse}),N("element","MatrixElement",new MatrixElementOptions{Row=0,Column=1}),N("precision_matrix","MatrixCreate",new MatrixCreateOptions{ValuesText="1.2345678901234567,0;0,1"}),N("display","CollectionDisplay",new CollectionDisplayOptions{Fields="Index,PointCount,Closed",MaximumOverlays=20})]);
            connections.AddRange([E("input","Image","affine_image","Image"),E("affine","Transform","affine_image","Transform"),E("input","Image","perspective_image","Image"),E("perspective","Transform","perspective_image","Transform"),E("input","Image","reference_roi","Image"),E("reference_roi","Collection","mapped_roi","Collection"),E("perspective","Transform","mapped_roi","Transform"),E("perspective","Transform","mapped_point","Transform"),E("mapped_roi","Collection","display","Collection"),E("perspective_image","TransformedImage","display","Image"),E("matrix","Matrix","inverse","A"),E("inverse","Matrix","element","Matrix")]);
        }
        else if(index==2)
        {
            nodes.AddRange([N("random","RandomNumber",new RandomNumberOptions{Kind=RandomNumberKind.Integer,Mode=RandomOutputMode.Collection,MinimumInteger=0,MaximumInteger=10,Count=4,Seed=260117}),N("select","CollectionSelect",new CollectionIndexOptions{Index=2}),N("value","ItemValue",new ItemValueOptions{Field=ItemValueField.Number}),N("lower","Compare",new CompareOptions{Operator=CompareOperator.GreaterOrEqual,ValueB=0}),N("upper","Compare",new CompareOptions{Operator=CompareOperator.LessThan,ValueB=10}),N("boolean","BooleanOperation",new BooleanOperationOptions{Operation=BooleanOperationKind.And}),N("result","ResultDisplay",new ResultDisplayParameters()),N("display","CollectionDisplay",new CollectionDisplayOptions{Fields="Index,Number",MaximumOverlays=0})]);
            connections.AddRange([E("random","Collection","select","Collection"),E("select","Item","value","Item"),E("value","Number","lower","ValueA"),E("value","Number","upper","ValueA"),E("lower","Result","boolean","A"),E("upper","Result","boolean","B"),E("boolean","Result","result","IsOk"),E("random","Collection","display","Collection")]);
        }
        else if(index==3)
        {
            nodes.AddRange([N("text","FileRead",new FileReadOptions{Path="sample.txt",Format=LocalFileFormat.Text}),N("json","FileRead",new FileReadOptions{Path="sample.json",Format=LocalFileFormat.Json}),N("csv","FileRead",new FileReadOptions{Path="sample.csv",Format=LocalFileFormat.Csv}),N("binary","FileRead",new FileReadOptions{Path="sample.bin",Format=LocalFileFormat.Binary}),N("whole_text","FileValue",new FileValueOptions()),N("json_number","FileValue",new FileValueOptions{Selection=FileValueSelection.JsonPointer,Target=FileValueTarget.Number,Pointer="/part/size"}),N("json_escaped","FileValue",new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/a~1b/~0key"}),N("csv_name","FileValue",new FileValueOptions{Selection=FileValueSelection.CsvCell,Row=0,Column=0}),N("csv_note","FileValue",new FileValueOptions{Selection=FileValueSelection.CsvCell,Row=0,Column=2}),N("binary_byte","FileValue",new FileValueOptions{Selection=FileValueSelection.BinaryByte,Target=FileValueTarget.Number,Offset=5}),N("text_display","TextDisplay",new TextDisplayParameters()),N("number_display","TextDisplay",new TextDisplayParameters{Prefix="JSON尺寸: "}),N("csv_display","TextDisplay",new TextDisplayParameters{Prefix="CSV备注: "})]);
            connections.AddRange([E("text","Result","whole_text","Result"),E("json","Result","json_number","Result"),E("json","Result","json_escaped","Result"),E("csv","Result","csv_name","Result"),E("csv","Result","csv_note","Result"),E("binary","Result","binary_byte","Result"),E("whole_text","Text","text_display","String"),E("json_number","Number","number_display","Number"),E("csv_note","Text","csv_display","String")]);
        }
        else throw new ArgumentOutOfRangeException(nameof(index));
        return new(){ProjectName=new[]{"低对比增强与真实梯度","矩阵与仿射/透视映射","随机数与统一布尔判定","受控文件读取与字段选择"}[index],Nodes=nodes,Connections=connections};
    }
    public static async Task SaveAllAsync(string newRoot,CancellationToken ct=default)
    {
        newRoot=Path.GetFullPath(newRoot);foreach(var id in Ids)if(Directory.Exists(Path.Combine(newRoot,id))||File.Exists(Path.Combine(newRoot,id)))throw new IOException("示例已存在，拒绝覆盖："+id);
        var store=new ProjectStore(BuiltinRegistration.CreateRegistry());
        for(var index=0;index<Ids.Length;index++)
        {
            ct.ThrowIfCancellationRequested();var directory=Path.Combine(newRoot,Ids[index]);Directory.CreateDirectory(directory);
            using var image=new Mat(240,320,index==0?MatType.CV_8UC1:MatType.CV_8UC3,Scalar.All(index==0?110:245));
            if(index==0){Cv2.Rectangle(image,new Rect(40,40,100,80),Scalar.All(120),-1);Cv2.Circle(image,new(235,165),30,Scalar.All(130),-1);}
            else if(index==1){for(var x=0;x<320;x+=20)Cv2.Line(image,new(x,0),new(x,239),new Scalar(200,210,225));for(var y=0;y<240;y+=20)Cv2.Line(image,new(0,y),new(319,y),new Scalar(200,210,225));Cv2.Rectangle(image,new Rect(50,60,60,40),new Scalar(30,100,230),-1);Cv2.Circle(image,new(230,155),30,new Scalar(20,170,40),-1);}
            else{for(var i=0;i<4;i++)Cv2.Rectangle(image,new Rect(30+i*70,60,45,120),new Scalar(210-i*20,175-i*15,120+i*20),-1);}
            if(index<2)Cv2.ImWrite(Path.Combine(directory,"input.png"),image);Cv2.ImWrite(Path.Combine(directory,"preview.png"),image);
            if(index==3)
            {
                await File.WriteAllTextAsync(Path.Combine(directory,"sample.txt"),"VisionStudio offline file sample\r\nSecond line: 42\r\n",new UTF8Encoding(true),ct);
                await File.WriteAllTextAsync(Path.Combine(directory,"sample.json"),"{\"part\":{\"name\":\"part-A\",\"ok\":true,\"size\":12.3456789012345},\"a/b\":{\"~key\":\"kept\"}}",new UTF8Encoding(false),ct);
                await File.WriteAllTextAsync(Path.Combine(directory,"sample.csv"),"name,count,note\r\n\"A,one\",2,\"quoted \"\"text\"\"\r\nsecond line\"\r\nB,3,normal\r\n",new UTF8Encoding(false),ct);
                await File.WriteAllBytesAsync(Path.Combine(directory,"sample.bin"),[0,1,2,127,128,255],ct);
            }
            var project=Create(index);await store.SaveAsync(Path.Combine(directory,"project.json"),project,ct);
            var instructions=new[]{"先复制到可写目录并运行。原图灰度110/120/130，经线性α=2、β=-220和0..40拉伸得到0/128/255，再Canny及全部轮廓。预期2个目标。点击raw_display.Image与stretch.ProcessedImage作同图对照；gradient_x.Derivative是真实Float32，Preview仅8位绝对值。", "复制后运行。仿射和透视各生成独立图像；mapped_roi把原ROI映射为真实四边形，来源键保留，不能当作旋转矩形。mapped_point给出(80,80)的正向映射，InverseTransform可还原。estimate使用明确配对点，不自动注册或标定。matrix/inverse/element展示真实数学矩阵；precision_matrix用于高精度表格编辑。", "固定种子生成4个[0,10)整数，以0基索引2显式选取；分别比较≥0和<10，接统一AND后业务判定OK。没有偷偷选择第一项；新会话复现，同会话再次运行推进随机流。预览是静态示意图，不是运行随机值或缺陷真值。", "运行后检查text/json/csv/binary的Result，FileValue分别选择全文、JSON指针、CSV行列和二进制字节。CSV含逗号、双引号转义和引号内换行；JSON指针/a~1b/~0key演示标准转义。资源全部工程相对路径，仅只读，不访问网络、密钥或用户业务文件。"}[index];
            await File.WriteAllTextAsync(Path.Combine(directory,"README.md"),$"# {project.ProjectName}\n\n{instructions}\n\n全部资源为自制合成/演示文本，不含用户数据、模型和环境，不代表产线准确率。普通核心Runtime即可运行，不需要Python、相机或网络。参数/旧结果失效沿用现有编辑器；工程包使用原导出/预检/导入到新目录。\n",ct);
            ExampleCheck[] expected=index switch{0=>[C("count","Count",2)],1=>[C("element","Value",-.7,1e-12),C("estimate","Residual",0d,1e-8),C("mapped_point","MappedPoint",91.2/1.088,1e-9,"X")],2=>[C("random","Count",4),C("boolean","Result",true),C("result","IsOk",true)],_=>[C("json_number","Number",12.3456789012345,1e-13),C("json_escaped","Text","kept"),C("csv_name","Text","A,one"),C("csv_note","Text","quoted \"text\"\r\nsecond line"),C("binary_byte","Number",255d)]};
            var manifest=new ExampleManifest{Id=Ids[index],Name=project.ProjectName,Category="本轮算子扩充",Description=instructions,Instructions=instructions,Verification=string.Join("；",expected.Select(x=>x.NodeId+"."+x.Port+"="+x.Expected)),Tags=["自制资源","离线",index<2?"图像与矩阵":"数据与逻辑"],Expected=expected};
            foreach(var file in Directory.EnumerateFiles(directory))manifest.Files[Path.GetFileName(file)]=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file,ct)));
            await File.WriteAllTextAsync(Path.Combine(directory,"manifest.json"),JsonSerializer.Serialize(manifest,JsonDefaults.Options),ct);
        }
    }
}

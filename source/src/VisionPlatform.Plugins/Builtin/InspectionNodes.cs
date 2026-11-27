using System.ComponentModel;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;
[SubflowSafe]
public sealed class ImageArithmeticNode:NodeBase<ImageArithmeticOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ImageArithmetic",[new("ImageA",typeof(ImageFrame)),new("ImageB",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("ProcessedImage",typeof(ImageFrame)),new("ReferenceFrameId",typeof(Guid))]);
    protected override IReadOnlyList<string> Validate(ImageArithmeticOptions p)=>InspectionProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {var b=c.Input<ImageFrame>("ImageB");var output=InspectionProcessors.Arithmetic(c.Input<ImageFrame>("ImageA"),b,c.Parameters<ImageArithmeticOptions>(),c.TryInput<RotatedRectRoi>("ROI",out var r)?r:null,ct);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["ProcessedImage"]=output,["ReferenceFrameId"]=b.FrameId},[output]));}
}
[SubflowSafe]
public sealed class ApplyMaskNode:NodeBase<InspectionEmptyOptions>
{
    protected override IReadOnlyList<string> Validate(InspectionEmptyOptions p)=>p.MaximumPixels is <1 or >100000000?["无效像素预算。"]:[];
    public override NodeDescriptor Descriptor=>Describe("ApplyMask",[new("Image",typeof(ImageFrame)),new("Mask",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("ProcessedImage",typeof(ImageFrame))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {var output=InspectionProcessors.Mask(c.Input<ImageFrame>("Image"),c.Input<ImageFrame>("Mask"),c.TryInput<RotatedRectRoi>("ROI",out var r)?r:null,ct,c.Parameters<InspectionEmptyOptions>().MaximumPixels);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["ProcessedImage"]=output},[output]));}
}
[SubflowSafe]
public sealed class RegionStatisticsNode:NodeBase<InspectionEmptyOptions>
{
    protected override IReadOnlyList<string> Validate(InspectionEmptyOptions p)=>p.MaximumPixels is <1 or >100000000?["无效像素预算。"]:[];
    public override NodeDescriptor Descriptor=>Describe("RegionStatistics",[new("Image",typeof(ImageFrame)),new("Mask",typeof(ImageFrame),false),new("ROI",typeof(RotatedRectRoi),false)],[new("Statistics",typeof(RegionStatistics)),new("Valid",typeof(bool)),new("PixelCount",typeof(int)),new("NonZeroCount",typeof(int)),new("Mean",typeof(double),false),new("StandardDeviation",typeof(double),false),new("ForegroundRatio",typeof(double),false)]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {var r=InspectionProcessors.Statistics(c.Input<ImageFrame>("Image"),c.TryInput<ImageFrame>("Mask",out var m)?m:null,c.TryInput<RotatedRectRoi>("ROI",out var roi)?roi:null,ct,c.Parameters<InspectionEmptyOptions>().MaximumPixels);var output=new Dictionary<string,object>{["Statistics"]=r,["Valid"]=r.Valid,["PixelCount"]=r.PixelCount,["NonZeroCount"]=r.NonZeroCount};if(r.Valid){output["Mean"]=r.Mean!.Value;output["StandardDeviation"]=r.StandardDeviation!.Value;output["ForegroundRatio"]=r.ForegroundRatio!.Value;}return ValueTask.FromResult(new NodeOutput(output));}
}
[SubflowSafe]
public sealed class ColorSpaceConvertNode:NodeBase<ColorConversionOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ColorSpaceConvert",[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("ColorData",typeof(ColorPlaneFrame)),new("Preview",typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(ColorConversionOptions p)=>Enum.IsDefined(p.ColorSpace)?[]:["无效工作颜色空间。"];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {using var v=RecognitionRegion.Open(c.Input<ImageFrame>("Image"),c.TryInput<RotatedRectRoi>("ROI",out var roi)?roi:null,16000000);var r=ColorProcessors.Convert(v,c.Parameters<ColorConversionOptions>(),ct);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["ColorData"]=r.Color,["Preview"]=r.Preview},[r.Color,r.Preview]));}
}
[SubflowSafe]
public sealed class ColorInRangeNode:NodeBase<ColorRangeOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ColorInRange",[new("ColorData",typeof(ColorPlaneFrame))],[new("BinaryImage",typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(ColorRangeOptions p)=>ColorProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct){var r=ColorProcessors.Range(c.Input<ColorPlaneFrame>("ColorData"),c.Parameters<ColorRangeOptions>(),ct);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["BinaryImage"]=r},[r]));}
}
public sealed record ColorChannelOptions{[DisplayName("通道索引0..2（HSV=H/S/V；Lab=L/a/b）")]public int Channel{get;init;}}
[SubflowSafe]
public sealed class ColorChannelNode:NodeBase<ColorChannelOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ColorChannel",[new("ColorData",typeof(ColorPlaneFrame))],[new("ChannelImage",typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(ColorChannelOptions p)=>p.Channel is <0 or >2?["通道索引为0..2。"]:[];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct){var r=ColorProcessors.Channel(c.Input<ColorPlaneFrame>("ColorData"),c.Parameters<ColorChannelOptions>().Channel,ct);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["ChannelImage"]=r},[r]));}
}
[SubflowSafe]
public sealed class FindContoursNode:NodeBase<FindContoursOptions>
{
    public override NodeDescriptor Descriptor=>Describe("FindContours",[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("Collection",typeof(VisionCollection)),new("Count",typeof(int))]);
    protected override IReadOnlyList<string> Validate(FindContoursOptions p)=>ShapeProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)=>CollectionOutput(ShapeProcessors.Contours(c.Input<ImageFrame>("Image"),c.Parameters<FindContoursOptions>(),c.TryInput<RotatedRectRoi>("ROI",out var r)?r:null,c.RunId,c.Spec.NodeId,ct));
    internal static ValueTask<NodeOutput> CollectionOutput(VisionCollection r)=>ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["Collection"]=r,["Count"]=r.Count}));
}
[SubflowSafe]
public sealed class ContourAnalysisNode:NodeBase<ShapeAnalysisOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ContourAnalysis",[new("Collection",typeof(VisionCollection))],[new("Collection",typeof(VisionCollection)),new("ConvexHulls",typeof(VisionCollection)),new("Approximations",typeof(VisionCollection)),new("Rectangles",typeof(VisionCollection)),new("Count",typeof(int))]);
    protected override IReadOnlyList<string> Validate(ShapeAnalysisOptions p)=>ShapeProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var r=ShapeProcessors.Analyze(c.Input<VisionCollection>("Collection"),c.Parameters<ShapeAnalysisOptions>(),ct);
        var contours=r.Items.Cast<ContourItem>().ToArray();var hulls=new VisionCollection(VisionItemKind.Contour,contours.Select(i=>i with{Points=i.Analysis!.ConvexHull,Analysis=null,RasterSource=null}));
        var approximation=new VisionCollection(VisionItemKind.Contour,contours.Select(i=>i with{Points=i.Analysis!.Approximation,Analysis=null,RasterSource=null}));var rects=new VisionCollection(VisionItemKind.Roi,contours.Where(i=>i.Region is not null).Select(i=>new RoiItem(i.Origin,i.Region!){OriginalItem=i}));
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["Collection"]=r,["ConvexHulls"]=hulls,["Approximations"]=approximation,["Rectangles"]=rects,["Count"]=r.Count}));
    }
}
[SubflowSafe]
public sealed class HoughLinesPNode:NodeBase<HoughLinesOptions>
{
    public override NodeDescriptor Descriptor=>Describe("HoughLinesP",[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("Collection",typeof(VisionCollection)),new("Count",typeof(int))]);
    protected override IReadOnlyList<string> Validate(HoughLinesOptions p)=>ShapeProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)=>FindContoursNode.CollectionOutput(ShapeProcessors.Lines(c.Input<ImageFrame>("Image"),c.Parameters<HoughLinesOptions>(),c.TryInput<RotatedRectRoi>("ROI",out var r)?r:null,c.RunId,c.Spec.NodeId,ct));
}
[SubflowSafe]
public sealed class HoughCirclesNode:NodeBase<HoughCirclesOptions>
{
    public override NodeDescriptor Descriptor=>Describe("HoughCircles",[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("Collection",typeof(VisionCollection)),new("Count",typeof(int))]);
    protected override IReadOnlyList<string> Validate(HoughCirclesOptions p)=>ShapeProcessors.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)=>FindContoursNode.CollectionOutput(ShapeProcessors.Circles(c.Input<ImageFrame>("Image"),c.Parameters<HoughCirclesOptions>(),c.TryInput<RotatedRectRoi>("ROI",out var r)?r:null,c.RunId,c.Spec.NodeId,ct));
}
public sealed record CannyOptions
{[DisplayName("Canny低阈值")]public double Low{get;init;}=50;[DisplayName("Canny高阈值")]public double High{get;init;}=150;[DisplayName("Sobel核3/5/7")]public int Aperture{get;init;}=3;[DisplayName("L2梯度范数")]public bool L2Gradient{get;init;}}
[SubflowSafe]
public sealed class CannyNode:NodeBase<CannyOptions>
{
    public override NodeDescriptor Descriptor=>Describe("Canny",[new("Image",typeof(ImageFrame)),new("ROI",typeof(RotatedRectRoi),false)],[new("Edges",typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(CannyOptions p)=>!double.IsFinite(p.Low)||!double.IsFinite(p.High)||p.Low<0||p.High<p.Low||p.High>100000||p.Aperture is not(3 or 5 or 7)?["无效Canny阈值或核大小。"]:[];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {var image=c.Input<ImageFrame>("Image");InspectionFrames.Check(image,true);using var v=RecognitionRegion.Open(image,c.TryInput<RotatedRectRoi>("ROI",out var roi)?roi:null,16000000);var p=c.Parameters<CannyOptions>();using var l=new VisionPlatform.Imaging.OpenCvImageLease(v);using var m=new Mat();ct.ThrowIfCancellationRequested();Cv2.Canny(l.Mat,m,p.Low,p.High,p.Aperture,p.L2Gradient);ct.ThrowIfCancellationRequested();var output=InspectionFrames.Copy(m,v);return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{["Edges"]=output},[output]));}
}
public sealed record ItemGeometryOptions{[DisplayName("明确的目标坐标空间")]public string CoordinateSpaceId{get;init;}="image";}
[SubflowSafe]
public sealed class ItemGeometryNode:NodeBase<ItemGeometryOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ItemGeometry",[new("Item",typeof(VisionItem))],[new("Line",typeof(LineGeometry),false),new("Circle",typeof(CircleGeometry),false),new("PointA",typeof(Point2D)),new("PointB",typeof(Point2D))]);
    protected override IReadOnlyList<string> Validate(ItemGeometryOptions p)=>string.IsNullOrWhiteSpace(p.CoordinateSpaceId)?["需要明确坐标空间。"]:[];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var item=c.Input<VisionItem>("Item");if(item.Origin.CoordinateSpaceId!=c.Parameters<ItemGeometryOptions>().CoordinateSpaceId)throw new ArgumentException("几何坐标空间不同；先显式变换或配置匹配的测量空间。");
        var o=new Dictionary<string,object>();if(item is LineItem{EndPoint:{}end} line){o["Line"]=line.Line;o["PointA"]=line.Line.Origin;o["PointB"]=end;}
        else if((item switch { CircleItem existing => existing.Circle, ContourEnvelopeItem envelope => envelope.Circle, _ => null }) is { } circle)
        {o["Circle"]=circle;o["PointA"]=circle.Center;o["PointB"]=new Point2D(circle.Center.X+circle.Radius,circle.Center.Y);}
        else throw new ArgumentException("请显式选择带端点的线段或圆；不默认选择集合第一项。");return ValueTask.FromResult(new NodeOutput(o));
    }
}

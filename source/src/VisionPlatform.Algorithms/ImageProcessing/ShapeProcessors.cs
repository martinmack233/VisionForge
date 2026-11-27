using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;
public enum ContourRetrieval { External, List, CComp, Tree }
public enum ContourChain { Simple, None }
public sealed record FindContoursOptions
{
    [DisplayName("轮廓检索模式")]public ContourRetrieval Retrieval{get;init;}=ContourRetrieval.Tree;
    [DisplayName("轮廓点压缩")]public ContourChain Chain{get;init;}=ContourChain.Simple;
    [DisplayName("最多轮廓数"),Range(1,10000)]public int MaximumContours{get;init;}=1000;
    [DisplayName("全部轮廓最多点数"),Range(3,2000000)]public int MaximumPoints{get;init;}=200000;
    [DisplayName("最多输入像素（大图请指定ROI）"),Range(1,100000000)]public int MaximumPixels{get;init;}=16000000;
}
public sealed record ShapeAnalysisOptions
{
    [DisplayName("多边形逼近误差（像素）"),Range(0,1000)]public double ApproximationEpsilon{get;init;}=2;
    [DisplayName("拟合椭圆（至少5点）")]public bool FitEllipse{get;init;}=true;
}
public sealed record HoughLinesOptions
{
    [DisplayName("rho步长（像素）"),Range(.1,100)]public double Rho{get;init;}=1;
    [DisplayName("角度步长（度）"),Range(.01,180)]public double ThetaDegrees{get;init;}=1;
    [DisplayName("投票阈值"),Range(1,100000)]public int Threshold{get;init;}=40;
    [DisplayName("最短线段（像素）"),Range(0,100000)]public double MinimumLength{get;init;}=30;
    [DisplayName("最大连接间隙（像素）"),Range(0,10000)]public double MaximumGap{get;init;}=5;
    [DisplayName("最多结果数"),Range(1,10000)]public int MaximumResults{get;init;}=1000;
}
public sealed record HoughCirclesOptions
{
    [DisplayName("累加器尺度dp"),Range(1,10)]public double Dp{get;init;}=1;
    [DisplayName("最小圆心距离（像素）"),Range(1,100000)]public double MinimumDistance{get;init;}=20;
    [DisplayName("内部Canny高阈值"),Range(.1,1000)]public double EdgeThreshold{get;init;}=100;
    [DisplayName("圆心累加器阈值"),Range(.1,1000)]public double CenterThreshold{get;init;}=20;
    [DisplayName("最小半径（像素）"),Range(0,100000)]public int MinimumRadius{get;init;}=5;
    [DisplayName("最大半径（像素）"),Range(1,100000)]public int MaximumRadius{get;init;}=1000;
    [DisplayName("最多结果数"),Range(1,10000)]public int MaximumResults{get;init;}=1000;
}
public static class ShapeProcessors
{
    public static IReadOnlyList<string> Validate(FindContoursOptions p)=>!Enum.IsDefined(p.Retrieval)||!Enum.IsDefined(p.Chain)||p.MaximumContours is <1 or >10000||p.MaximumPoints is <3 or >2000000||p.MaximumPixels is <1 or >100000000? ["无效轮廓模式或资源预算。"]:[];
    public static IReadOnlyList<string> Validate(ShapeAnalysisOptions p)=>!double.IsFinite(p.ApproximationEpsilon)||p.ApproximationEpsilon<0||p.ApproximationEpsilon>1000?["逼近误差必须为0..1000像素。"]:[];
    public static IReadOnlyList<string> Validate(HoughLinesOptions p)=>!double.IsFinite(p.Rho)||p.Rho is <.1 or >100||!double.IsFinite(p.ThetaDegrees)||p.ThetaDegrees is <.01 or >180||p.Threshold is <1 or >100000||!double.IsFinite(p.MinimumLength)||p.MinimumLength is <0 or >100000||!double.IsFinite(p.MaximumGap)||p.MaximumGap is <0 or >10000||p.MaximumResults is <1 or >10000?["无效霍夫线段参数或结果预算。"]:[];
    public static IReadOnlyList<string> Validate(HoughCirclesOptions p)=>new[]{p.Dp,p.MinimumDistance,p.EdgeThreshold,p.CenterThreshold}.Any(x=>!double.IsFinite(x))||p.Dp is <1 or >10||p.MinimumDistance is <1 or >100000||p.EdgeThreshold is <.1 or >1000||p.CenterThreshold is <.1 or >1000||p.MinimumRadius<0||p.MaximumRadius<p.MinimumRadius||p.MaximumRadius is <1 or >100000||p.MaximumResults is <1 or >10000?["无效霍夫圆参数/半径范围或结果预算。"]:[];
    private static void Invalid(IReadOnlyList<string> e){if(e.Count>0)throw new ArgumentException(string.Join(";",e));}
    public static VisionCollection Contours(ImageFrame image,FindContoursOptions p,RotatedRectRoi? roi,Guid run,string node,CancellationToken ct)
    {
        Invalid(Validate(p));InspectionFrames.Check(image,true);using var v=RecognitionRegion.Open(image,roi,p.MaximumPixels);using var l=new OpenCvImageLease(v);ct.ThrowIfCancellationRequested();InspectionFrames.Binary(l.Mat);
        var mode=p.Retrieval switch{ContourRetrieval.External=>RetrievalModes.External,ContourRetrieval.List=>RetrievalModes.List,ContourRetrieval.CComp=>RetrievalModes.CComp,_=>RetrievalModes.Tree};
        Cv2.FindContours(l.Mat,out Point[][] points,out HierarchyIndex[] hierarchy,mode,p.Chain==ContourChain.Simple?ContourApproximationModes.ApproxSimple:ContourApproximationModes.ApproxNone);
        ct.ThrowIfCancellationRequested();if(points.Length>p.MaximumContours||points.Sum(x=>(long)x.Length)>p.MaximumPoints)throw new ArgumentException("真实轮廓结果超过预算，请缩小ROI；未丢弃目标或只选最大项。");
        var items=new List<ContourItem>();
        var raster = new ContourRasterSource(image.BufferHandle.BufferId, RasterBounds(image), RasterBounds(v),
            p.Retrieval is ContourRetrieval.Tree or ContourRetrieval.CComp);
        for(var i=0;i<points.Length;i++)
        {
            ct.ThrowIfCancellationRequested();var depth=0;var parent=hierarchy[i].Parent;var a=parent;while(a>=0){if(++depth>hierarchy.Length)throw new InvalidDataException("轮廓层级异常。");a=hierarchy[a].Parent;}
            items.Add(new(InspectionFrames.Origin(run,node,i,image),Array.AsReadOnly(points[i].Select(pt=>new Point2D(pt.X+v.CoordinateSpace.OriginX,pt.Y+v.CoordinateSpace.OriginY)).ToArray()),true){ParentIndex=parent,IsHole=p.Retrieval==ContourRetrieval.List?null:depth%2==1,RasterSource=raster});
        }
        return new(VisionItemKind.Contour,items);
    }
    public static VisionCollection Analyze(VisionCollection contours,ShapeAnalysisOptions p,CancellationToken ct)
    {
        Invalid(Validate(p));if(contours.Kind!=VisionItemKind.Contour)throw new ArgumentException("形状分析需要真实轮廓集合，不将Blob边界框冒充轮廓。");
        var output=new List<VisionItem>();
        foreach(var item in contours.Items.Cast<ContourItem>())
        {
            ct.ThrowIfCancellationRequested();if(item.Points.Count>200000||item.Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)))throw new ArgumentException("轮廓点超预算或非有限。");
            if(!item.Closed||item.Points.Count<3){output.Add(item with{Analysis=new(null,null,null,null,null,null,[],[],null,null,null,"开放轮廓或不足3点，面积/形状指标未定义。")});continue;}
            // Translate before float-based OpenCV geometry to avoid large-origin float precision loss.
            var offset=item.Points[0];var pts=item.Points.Select(q=>new Point2f((float)(q.X-offset.X),(float)(q.Y-offset.Y))).ToArray();
            Point2D Global(Point2f pt)=>new(pt.X+offset.X,pt.Y+offset.Y);
            var signed=Cv2.ContourArea(pts,true);var area=Math.Abs(signed);var perimeter=Cv2.ArcLength(pts,true);
            var bounds = AxisAlignedRectangle(pts, offset);
            var min = MinimumRectangle(pts, offset, item.Origin.CoordinateSpaceId);
            var hull=Cv2.ConvexHull(pts);var approx=Cv2.ApproxPolyDP(pts,p.ApproximationEpsilon,true);EllipseGeometry? ellipse=null;string? detail=null;
            if(p.FitEllipse)
            {
                if(pts.Length<5||area<1e-8)detail="椭圆拟合需要至少5个非退化点；其余有效指标保留。";
                else{try{var fit=Cv2.FitEllipse(pts);if(fit.Size.Width>1e-8&&fit.Size.Height>1e-8&&float.IsFinite(fit.Angle))ellipse=new(Global(fit.Center),fit.Size.Width,fit.Size.Height,fit.Angle,item.Origin.CoordinateSpaceId);else detail="椭圆拟合退化。";}catch(OpenCVException ex){detail="椭圆拟合失败："+ex.Message;}}
            }
            output.Add(item with{Analysis=new(area>1e-8?area:null,area>1e-8?signed:null,perimeter>1e-8?perimeter:null,bounds,min,ellipse,Array.AsReadOnly(hull.Select(Global).ToArray()),Array.AsReadOnly(approx.Select(Global).ToArray()),area>1e-8&&perimeter>1e-8?4*Math.PI*area/(perimeter*perimeter):null,min is null?null:min.Width/min.Height,min is null||area<1e-8?null:area/(min.Width*min.Height),area<1e-8?"零面积退化轮廓，面积比指标未定义。":detail)});
        }
        return new(VisionItemKind.Contour,output);
    }

    internal static RectRoi RasterBounds(ImageFrame image) =>
        new(image.CoordinateSpace.OriginX, image.CoordinateSpace.OriginY, image.Width, image.Height);

    internal static RectRoi AxisAlignedRectangle(Point2f[] points, Point2D offset)
    {
        var b = Cv2.BoundingRect(points);
        return new(b.X + offset.X, b.Y + offset.Y, b.Width, b.Height);
    }

    internal static RotatedRectRoi? MinimumRectangle(Point2f[] points, Point2D offset, string space)
    {
        var rect = Cv2.MinAreaRect(points);
        // Canonical long-axis rectangle: width >= height, clockwise angle in [-90,90).
        var width = (double)rect.Size.Width;
        var height = (double)rect.Size.Height;
        var angle = (double)rect.Angle;
        if (width < height) { (width, height) = (height, width); angle += 90; }
        angle = ((angle + 90) % 180 + 180) % 180 - 90;
        return width > 1e-8 && height > 1e-8
            ? new(rect.Center.X + offset.X, rect.Center.Y + offset.Y, width, height, angle, space)
            : null;
    }
    public static VisionCollection Lines(ImageFrame image,HoughLinesOptions p,RotatedRectRoi? roi,Guid run,string node,CancellationToken ct)
    {
        Invalid(Validate(p));InspectionFrames.Check(image,true);using var v=RecognitionRegion.Open(image,roi,16000000);using var l=new OpenCvImageLease(v);ct.ThrowIfCancellationRequested();InspectionFrames.Binary(l.Mat);
        var lines=Cv2.HoughLinesP(l.Mat,p.Rho,p.ThetaDegrees*Math.PI/180,p.Threshold,p.MinimumLength,p.MaximumGap);ct.ThrowIfCancellationRequested();if(lines.Length>p.MaximumResults)throw new ArgumentException("霍夫线段超过结果预算，未静默截断。");
        return new(VisionItemKind.Line,lines.Select((s,i)=>{var a=new Point2D(s.P1.X+v.CoordinateSpace.OriginX,s.P1.Y+v.CoordinateSpace.OriginY);var b=new Point2D(s.P2.X+v.CoordinateSpace.OriginX,s.P2.Y+v.CoordinateSpace.OriginY);var length=Math.Sqrt(Math.Pow(b.X-a.X,2)+Math.Pow(b.Y-a.Y,2));if(length<=0)throw new InvalidDataException("霍夫返回退化线段。");return new LineItem(InspectionFrames.Origin(run,node,i,image),new(a,new((b.X-a.X)/length,(b.Y-a.Y)/length),image.CoordinateSpace.Id)){EndPoint=b};}));
    }
    public static VisionCollection Circles(ImageFrame image,HoughCirclesOptions p,RotatedRectRoi? roi,Guid run,string node,CancellationToken ct)
    {
        Invalid(Validate(p));InspectionFrames.Check(image,true);using var v=RecognitionRegion.Open(image,roi,16000000);using var l=new OpenCvImageLease(v);ct.ThrowIfCancellationRequested();
        var circles=Cv2.HoughCircles(l.Mat,HoughModes.Gradient,p.Dp,p.MinimumDistance,p.EdgeThreshold,p.CenterThreshold,p.MinimumRadius,p.MaximumRadius);ct.ThrowIfCancellationRequested();if(circles.Length>p.MaximumResults)throw new ArgumentException("霍夫圆超过结果预算。");
        return new(VisionItemKind.Circle,circles.Select((s,i)=>new CircleItem(InspectionFrames.Origin(run,node,i,image),new(new(s.Center.X+v.CoordinateSpace.OriginX,s.Center.Y+v.CoordinateSpace.OriginY),s.Radius,image.CoordinateSpace.Id))));
    }
}

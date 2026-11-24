using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Imaging;
using VisionPlatform.Algorithms.Recognition;

namespace VisionPlatform.Algorithms.ImageProcessing;
public enum ImageArithmeticOperation { AbsoluteDifference, Add, Subtract, Weighted }
public sealed record ImageArithmeticOptions
{
    [DisplayName("8位运算（饱和至0..255）")] public ImageArithmeticOperation Operation { get; init; }
    [DisplayName("明确允许已对齐的不同帧")] public bool AllowDifferentFrames { get; init; }
    [DisplayName("权重A"),Range(-1000,1000)] public double Alpha { get; init; }=1;
    [DisplayName("权重B"),Range(-1000,1000)] public double Beta { get; init; }=1;
    [DisplayName("偏置"),Range(-1000,1000)] public double Gamma { get; init; }
}
public sealed record InspectionEmptyOptions
{
    [DisplayName("最多处理像素（大图请指定ROI）"),Range(1,100000000)] public int MaximumPixels{get;init;}=100000000;
}


public static class InspectionFrames
{
    public static void Check(ImageFrame frame, bool mono=false)
    {
        if (frame.PixelFormat.IsBayer() || mono && frame.PixelFormat!=PixelFormat.Mono8) throw new ArgumentException(mono ? "需要Mono8，请显式转换；不隐式灰度化。" : "Bayer必须先显式转换为RGB/BGR。");
    }
    public static void Pair(ImageFrame a, ImageFrame b, bool differentFrames=false, bool sameFormat=true)
    {
        Check(a);Check(b);
        if (a.Width!=b.Width || a.Height!=b.Height || a.CoordinateSpace!=b.CoordinateSpace || sameFormat&&a.PixelFormat!=b.PixelFormat)
            throw new ArgumentException("图像尺寸、格式或坐标空间/原点不同；必须显式裁切/转换/对齐，不静默缩放。");
        if (!differentFrames && a.FrameId!=b.FrameId) throw new ArgumentException("来源帧不同；仅已显式对齐的参考图可明确允许不同帧。");
    }
    public static unsafe ImageFrame Copy(Mat mat, ImageFrame source, PixelFormat? format=null)
    {
        var f=format??source.PixelFormat;
        using var b=new ImageFrameBuilder(mat.Width,mat.Height,f,coordinateSpace:source.CoordinateSpace);
        using var target=Mat.FromPixelData(mat.Rows,mat.Cols,mat.Type(),b.DataPointer,b.Stride);mat.CopyTo(target);
        return b.Build(source.FrameId,source.Timestamp);
    }
    public static void Binary(Mat source)
    {
        using var invalid=new Mat();Cv2.InRange(source,new Scalar(1),new Scalar(254),invalid);
        if (Cv2.CountNonZero(invalid)!=0) throw new ArgumentException("需要0/255二值图，请先使用阈值或Canny节点。");
    }
    public static ItemOrigin Origin(Guid run,string node,int index,ImageFrame image) => new(run,node,index,image.FrameId,image.CoordinateSpace.Id,$"{run:N}/{node}/{index}");
}
public static class InspectionProcessors
{
    public static IReadOnlyList<string> Validate(ImageArithmeticOptions p) => !Enum.IsDefined(p.Operation)||new[]{p.Alpha,p.Beta,p.Gamma}.Any(x=>!double.IsFinite(x)||Math.Abs(x)>1000) ? ["无效8位图像运算或非有限/超范围权重。"]:[];
    public static ImageFrame Arithmetic(ImageFrame a,ImageFrame b,ImageArithmeticOptions p,RotatedRectRoi? roi,CancellationToken ct)
    {
        if(Validate(p).Count>0)throw new ArgumentException(string.Join(";",Validate(p)));InspectionFrames.Pair(a,b,p.AllowDifferentFrames);
        using var av=RecognitionRegion.Open(a,roi);using var bv=RecognitionRegion.Open(b,roi);using var second=new OpenCvImageLease(bv);
        return ProcessingFrame.Execute(av,(input,output)=>{switch(p.Operation){case ImageArithmeticOperation.AbsoluteDifference:Cv2.Absdiff(input,second.Mat,output);break;case ImageArithmeticOperation.Add:Cv2.Add(input,second.Mat,output);break;case ImageArithmeticOperation.Subtract:Cv2.Subtract(input,second.Mat,output);break;case ImageArithmeticOperation.Weighted:Cv2.AddWeighted(input,p.Alpha,second.Mat,p.Beta,p.Gamma,output);break;}},ct);
    }
    public static ImageFrame Mask(ImageFrame image,ImageFrame mask,RotatedRectRoi? roi,CancellationToken ct,int maximumPixels=100000000)
    {
        if(maximumPixels is <1 or >100000000)throw new ArgumentException("无效图像处理像素预算。");
        InspectionFrames.Pair(image,mask,sameFormat:false);InspectionFrames.Check(mask,true);
        using var view=RecognitionRegion.Open(image,roi,maximumPixels);using var mv=RecognitionRegion.Open(mask,roi,maximumPixels);using var m=new OpenCvImageLease(mv);
        return ProcessingFrame.Execute(view,(a,o)=>{o.SetTo(Scalar.All(0));a.CopyTo(o,m.Mat);},ct);
    }
    public static RegionStatistics Statistics(ImageFrame image,ImageFrame? mask,RotatedRectRoi? roi,CancellationToken ct,int maximumPixels=100000000)
    {
        if(maximumPixels is <1 or >100000000)throw new ArgumentException("无效统计像素预算。");
        InspectionFrames.Check(image,true);if(mask is not null){InspectionFrames.Pair(image,mask);InspectionFrames.Check(mask,true);}
        using var view=RecognitionRegion.Open(image,roi,maximumPixels);using var input=new OpenCvImageLease(view);using var mv=mask is null?null:RecognitionRegion.Open(mask,roi);using var ml=mv is null?null:new OpenCvImageLease(mv);
        ct.ThrowIfCancellationRequested();var count=ml is null?checked(view.Width*view.Height):Cv2.CountNonZero(ml.Mat);
        if(count==0)return new(false,null,null,0,0,null,image.FrameId,view.CoordinateSpace,"掩膜没有选中像素，均值/比例未定义。");
        Scalar mean, std;
        if(ml is null) Cv2.MeanStdDev(input.Mat,out mean,out std);
        else Cv2.MeanStdDev(input.Mat,out mean,out std,ml.Mat);
        using var nonzero=new Mat();Cv2.Compare(input.Mat,Scalar.All(0),nonzero,CmpTypes.NE);
        if(ml is not null)Cv2.BitwiseAnd(nonzero,ml.Mat,nonzero);
        var n=Cv2.CountNonZero(nonzero);ct.ThrowIfCancellationRequested();return new(true,mean.Val0,std.Val0,count,n,n/(double)count,image.FrameId,view.CoordinateSpace,null);
    }
}
public sealed record ColorConversionOptions { [DisplayName("工作颜色空间（8位编码）")] public WorkingColorSpace ColorSpace {get;init;} }
public sealed record ColorRangeOptions
{
    [DisplayName("通道0下限（HSV H=0..179）"),Range(0,255)] public int Lower0{get;init;}
    [DisplayName("通道1下限"),Range(0,255)] public int Lower1{get;init;}
    [DisplayName("通道2下限"),Range(0,255)] public int Lower2{get;init;}
    [DisplayName("通道0上限（HSV H=0..179）"),Range(0,255)] public int Upper0{get;init;}=179;
    [DisplayName("通道1上限"),Range(0,255)] public int Upper1{get;init;}=255;
    [DisplayName("通道2上限"),Range(0,255)] public int Upper2{get;init;}=255;
}
public static class ColorProcessors
{
    public static (ColorPlaneFrame Color,ImageFrame Preview) Convert(ImageFrame image,ColorConversionOptions p,CancellationToken ct)
    {
        if(image.PixelFormat is not(PixelFormat.RGB8 or PixelFormat.BGR8)||!Enum.IsDefined(p.ColorSpace))throw new ArgumentException("颜色转换需要RGB8/BGR8，Bayer须先显式插值。");
        ct.ThrowIfCancellationRequested();using var source=new OpenCvImageLease(image);using var raw=new Mat();using var preview=new Mat();
        var code=p.ColorSpace==WorkingColorSpace.Hsv8?(image.PixelFormat==PixelFormat.RGB8?ColorConversionCodes.RGB2HSV:ColorConversionCodes.BGR2HSV):(image.PixelFormat==PixelFormat.RGB8?ColorConversionCodes.RGB2Lab:ColorConversionCodes.BGR2Lab);
        Cv2.CvtColor(source.Mat,raw,code);Cv2.CvtColor(raw,preview,p.ColorSpace==WorkingColorSpace.Hsv8?ColorConversionCodes.HSV2BGR:ColorConversionCodes.Lab2BGR);
        ct.ThrowIfCancellationRequested();using var storage=InspectionFrames.Copy(raw,image,PixelFormat.BGR8);var color=new ColorPlaneFrame(storage,p.ColorSpace);
        try{return(color,InspectionFrames.Copy(preview,image,PixelFormat.BGR8));}catch{color.Dispose();throw;}
    }
    public static IReadOnlyList<string> Validate(ColorRangeOptions p) => new[]{p.Lower0,p.Lower1,p.Lower2,p.Upper0,p.Upper1,p.Upper2}.Any(x=>x<0||x>255)||p.Lower0>p.Upper0||p.Lower1>p.Upper1||p.Lower2>p.Upper2 ? ["颜色范围必须0..255且各下限不大于上限；跨Hue零点请显式使用两个范围。"]:[];
    public static ImageFrame Range(ColorPlaneFrame color,ColorRangeOptions p,CancellationToken ct)
    {
        if(Validate(p).Count>0||color.ColorSpace==WorkingColorSpace.Hsv8&&p.Upper0>179)throw new ArgumentException("无效颜色范围；HSV H=0..179，S/V=0..255；Lab编码三通道0..255，a/b以128为零。");
        using var l=color.Lease();using var m=Mat.FromPixelData(color.Height,color.Width,MatType.CV_8UC3,l.DataPointer,color.Stride);using var result=new Mat();ct.ThrowIfCancellationRequested();Cv2.InRange(m,new(p.Lower0,p.Lower1,p.Lower2),new(p.Upper0,p.Upper1,p.Upper2),result);
        using var b=new ImageFrameBuilder(color.Width,color.Height,PixelFormat.Mono8,coordinateSpace:color.CoordinateSpace);using var target=Mat.FromPixelData(color.Height,color.Width,MatType.CV_8UC1,b.DataPointer,b.Stride);result.CopyTo(target);ct.ThrowIfCancellationRequested();return b.Build(color.FrameId,color.Timestamp);
    }
    public static ImageFrame Channel(ColorPlaneFrame color,int index,CancellationToken ct)
    {
        if(index<0||index>2)throw new ArgumentException("颜色通道索引为0..2。");using var l=color.Lease();using var m=Mat.FromPixelData(color.Height,color.Width,MatType.CV_8UC3,l.DataPointer,color.Stride);using var result=new Mat();ct.ThrowIfCancellationRequested();Cv2.ExtractChannel(m,result,index);
        using var b=new ImageFrameBuilder(color.Width,color.Height,PixelFormat.Mono8,coordinateSpace:color.CoordinateSpace);using var target=Mat.FromPixelData(color.Height,color.Width,MatType.CV_8UC1,b.DataPointer,b.Stride);result.CopyTo(target);ct.ThrowIfCancellationRequested();return b.Build(color.FrameId,color.Timestamp);
    }
}

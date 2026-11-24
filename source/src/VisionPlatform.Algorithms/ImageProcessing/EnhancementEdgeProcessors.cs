using VisionPlatform.Contracts.Results;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;

public record EnhancementBudgetOptions
{
    [DisplayName("最大处理像素数"),Range(1,100000000)]
    [Description("只处理指定ROI并输出该区域。默认1600万像素；大图请指定ROI，最大1亿像素。不复制区域外原图。")]
    public int MaximumPixels{get;init;}=16_000_000;
}
public sealed record LinearContrastOptions:EnhancementBudgetOptions
{
    [DisplayName("对比度增益 α"),Range(-16,16)]
    [Description("输出=round(α×输入+β)，使用最近偶数舍入后饱和到0..255。RGB/BGR对所有通道使用同一公式，不改变通道顺序。")]
    public double Alpha{get;init;}=1;
    [DisplayName("亮度偏移 β（灰度级）"),Range(-4096,4096)]public double Beta{get;init;}
}
public sealed record RangeStretchOptions:EnhancementBudgetOptions
{
    [DisplayName("输入下限"),Range(0,255),Description("小于等于此灰度级映射为输出下限；必须严格小于输入上限。RGB/BGR共用此映射，非独立直方图均衡。")]
    public double InputMinimum{get;init;}
    [DisplayName("输入上限"),Range(0,255),Description("大于等于此灰度级映射为输出上限；中间线性插值。上下限相同会报错，常量图按同一公式输出常量。")]
    public double InputMaximum{get;init;}=255;
    [DisplayName("输出下限"),Range(0,255),Description("8位输出灰度级，范围0..255，严格小于输出上限；最近偶数舍入并饱和截断。")]
    public double OutputMinimum{get;init;}
    [DisplayName("输出上限"),Range(0,255),Description("8位输出上限，范围0..255，严格大于输出下限。")]
    public double OutputMaximum{get;init;}=255;
}
public sealed record PercentileStretchOptions:EnhancementBudgetOptions
{
    [DisplayName("低百分位（%）"),Range(0,100),Description("仅Mono8。统计ROI全部像素的256桶精确直方图，选择从0开始的floor(p/100×(N−1))秩；不采样，不使用显示归一化。")]
    public double LowPercentile{get;init;}=1;
    [DisplayName("高百分位（%）"),Range(0,100),Description("必须高于低百分位。实际选中上下限在Statistics输出；若两者相同，全部输出OutputMinimum并标记ConstantRange。")]
    public double HighPercentile{get;init;}=99;
    [DisplayName("输出下限"),Range(0,255),Description("输出低端灰度级；必须小于输出上限。")]
    public double OutputMinimum{get;init;}
    [DisplayName("输出上限"),Range(0,255),Description("输出高端灰度级；位于0..255，超出所选输入范围的值截断到输出端点。")]
    public double OutputMaximum{get;init;}=255;
}
public sealed record GammaCorrectionOptions:EnhancementBudgetOptions
{
    [DisplayName("Gamma 指数 γ"),Range(.01,10)]
    [Description("输出=round(255×(输入/255)^γ)，γ<1提亮、γ>1变暗；RGB/BGR各通道使用同一LUT，可能改变色度，不进行逐通道直方图均衡。")]
    public double Gamma{get;init;}=1;
}
/// <summary>Deterministic byte LUTs. Statistics are calculated from the actual image, never preview normalization.</summary>
public static class EnhancementProcessors
{
    public static IReadOnlyList<string> Validate(EnhancementBudgetOptions p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors=new List<string>();if(p.MaximumPixels is <1 or >100_000_000)errors.Add("最大处理像素数必须为1..100000000。");
        static bool In(double v,double min,double max)=>double.IsFinite(v)&&v>=min&&v<=max;
        switch(p)
        {
            case LinearContrastOptions o when !In(o.Alpha,-16,16)||!In(o.Beta,-4096,4096):errors.Add("α必须为-16..16，β为-4096..4096，且必须为有限数值。");break;
            case RangeStretchOptions o when !In(o.InputMinimum,0,255)||!In(o.InputMaximum,0,255)||o.InputMinimum>=o.InputMaximum||!In(o.OutputMinimum,0,255)||!In(o.OutputMaximum,0,255)||o.OutputMinimum>=o.OutputMaximum:errors.Add("范围拉伸要求0≤输入下限<输入上限≤255、0≤输出下限<输出上限≤255；相同上下限无效。");break;
            case PercentileStretchOptions o when !In(o.LowPercentile,0,100)||!In(o.HighPercentile,0,100)||o.LowPercentile>=o.HighPercentile||!In(o.OutputMinimum,0,255)||!In(o.OutputMaximum,0,255)||o.OutputMinimum>=o.OutputMaximum:errors.Add("百分位要求0≤低百分位<高百分位≤100；输出范围必须严格递增且位于0..255。");break;
            case GammaCorrectionOptions o when !In(o.Gamma,.01,10):errors.Add("Gamma必须为0.01..10的有限指数。");break;
        }
        return errors;
    }
    private static byte Byte(double v)=>(byte)Math.Clamp(Math.Round(v,MidpointRounding.ToEven),0,255);
    private static long[][] Histogram(ImageFrame frame,CancellationToken ct)
    {
        var n=frame.PixelFormat.BytesPerPixel();var h=Enumerable.Range(0,n).Select(_=>new long[256]).ToArray();using var lease=frame.Lease();
        for(var y=0;y<frame.Height;y++){ct.ThrowIfCancellationRequested();var row=lease.GetRowSpan(y);for(var x=0;x<row.Length;x++)h[x%n][row[x]]++;}return h;
    }
    private static (double Minimum,double Maximum,double Mean,double Std) Statistics(long[] histogram)
    {
        var count=histogram.Sum();double sum=0,sum2=0;for(var i=0;i<256;i++){sum+=i*(double)histogram[i];sum2+=i*(double)i*histogram[i];}
        var mean=sum/count;return(Array.FindIndex(histogram,v=>v>0),Array.FindLastIndex(histogram,v=>v>0),mean,Math.Sqrt(Math.Max(0,sum2/count-mean*mean)));
    }
    public static EnhancementStatistics Describe(ImageFrame input,ImageFrame output,string rule,CancellationToken ct=default,double? low=null,double? high=null,bool constant=false)
    {
        InspectionFrames.Pair(input,output);var before=Histogram(input,ct);var after=Histogram(output,ct);var channels=before.Select((h,i)=>{var a=Statistics(h);var b=Statistics(after[i]);return new EnhancementChannelStatistics(i,a.Minimum,a.Maximum,a.Mean,a.Std,b.Minimum,b.Maximum,b.Mean,b.Std);}).ToArray();
        return new(input.FrameId,input.CoordinateSpace,input.Width,input.Height,rule,Array.AsReadOnly(channels),low,high,constant);
    }
    public static (ImageFrame Image,EnhancementStatistics Statistics) Apply(ImageFrame image,EnhancementBudgetOptions options,RotatedRectRoi? roi,CancellationToken ct)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));InspectionFrames.Check(image);ct.ThrowIfCancellationRequested();using var view=RecognitionRegion.Open(image,roi,options.MaximumPixels);
        double? low=null,high=null;var constant=false;string rule;Func<double,double> map;
        switch(options)
        {
            case LinearContrastOptions p:map=v=>p.Alpha*v+p.Beta;rule="round-to-even(α×v+β), saturate[0,255]; common mapping on all channels";break;
            case RangeStretchOptions p:low=p.InputMinimum;high=p.InputMaximum;map=v=>p.OutputMinimum+Math.Clamp((v-p.InputMinimum)/(p.InputMaximum-p.InputMinimum),0,1)*(p.OutputMaximum-p.OutputMinimum);rule="clamped input-range linear mapping, round-to-even and saturate[0,255]; common mapping on all channels";break;
            case PercentileStretchOptions p:
                InspectionFrames.Check(view,true);var histogram=Histogram(view,ct)[0];var count=histogram.Sum();
                int Quantile(double percentile){var rank=(long)Math.Floor(percentile/100*(count-1));long sum=0;for(var i=0;i<256;i++){sum+=histogram[i];if(sum>rank)return i;}throw new InvalidOperationException("百分位统计为空。");}
                var lo=Quantile(p.LowPercentile);var hi=Quantile(p.HighPercentile);low=lo;high=hi;constant=lo==hi;
                map=v=>lo==hi?p.OutputMinimum:p.OutputMinimum+Math.Clamp((v-lo)/(hi-lo),0,1)*(p.OutputMaximum-p.OutputMinimum);
                rule="Mono8 exact histogram of every ROI pixel; rank=floor(p/100×(N−1)); constant quantiles map to OutputMinimum";break;
            case GammaCorrectionOptions p:map=v=>255*Math.Pow(v/255,p.Gamma);rule="round-to-even(255×(v/255)^γ), saturate[0,255]; γ<1 brighter, γ>1 darker; same LUT on RGB/BGR channels";break;
            default:map=v=>255-v;rule="255−v, unchanged pixel format/channel order";break;
        }
        var lut=Enumerable.Range(0,256).Select(v=>Byte(map(v))).ToArray();using var builder=new ImageFrameBuilder(view.Width,view.Height,view.PixelFormat,coordinateSpace:view.CoordinateSpace);using(var lease=view.Lease())for(var y=0;y<view.Height;y++){ct.ThrowIfCancellationRequested();var src=lease.GetRowSpan(y);var target=builder.GetRowSpan(y);for(var x=0;x<src.Length;x++)target[x]=lut[src[x]];}
        var result=builder.Build(view.FrameId,view.Timestamp);try{return(result,Describe(view,result,rule,ct,low,high,constant));}catch{result.Dispose();throw;}
    }
}

public enum EdgeBorder
{
    [Display(Name="反射（不重复边界）",Description="OpenCV Reflect101；只使用ROI内像素扩展。") ]Reflect101,
    [Display(Name="反射（重复边界）",Description="OpenCV Reflect；ROI边界像素重复参与对称扩展。") ]Reflect,
    [Display(Name="复制边界",Description="OpenCV Replicate；使用最近边界像素扩展。") ]Replicate,
    [Display(Name="零填充",Description="ROI外按0填充，可能形成强边界。") ]ConstantZero
}
public record DerivativeOptions
{
    [DisplayName("导数缩放 scale"),Range(-1000000,1000000),Description("真实带符号Float32导数乘此系数；不进行8位截断。Preview另行取绝对值并饱和到255。")]
    public double Scale{get;init;}=1;
    [DisplayName("导数偏移 delta"),Range(-1000000,1000000),Description("乘scale后加入真实导数的有限偏移；保留小数与符号。")]
    public double Delta{get;init;}
    [DisplayName("ROI独立边界处理")]
    [Description("只使用ROI内像素。ROI外真实像素不参与卷积；默认Reflect101。不等于先整图求导再裁切。")]
    public EdgeBorder Border{get;init;}=EdgeBorder.Reflect101;
    [DisplayName("最大处理像素数"),Range(1,16000000),Description("最多1600万像素；8192×10000等大图必须指定有界ROI，避免为每个项目复制整图。")]
    public int MaximumPixels{get;init;}=16_000_000;
}
public sealed record SobelOptions:DerivativeOptions
{
    [DisplayName("X导数阶数"),Range(0,6),Description("图像X向右。Dx=1,Dy=0为一阶X梯度；其他阶数组合是高阶导数，不能冒充幅值节点的一阶输入。")]
    public int Dx{get;init;}=1;
    [DisplayName("Y导数阶数"),Range(0,6),Description("图像Y向下。Dx=0,Dy=1为一阶Y梯度；至少一个阶数大于0，每方向阶数须小于核大小。")]
    public int Dy{get;init;}
    [DisplayName("核大小（1/3/5/7）"),Range(1,7),Description("核1仅支持单方向一阶或二阶导数；核3/5/7每方向阶数必须小于核大小。仅接受Mono8，RGB/Bayer需显式转换。")]
    public int KernelSize{get;init;}=3;
}
public enum ScharrDirection
{
    [Display(Name="X方向一阶",Description="X向右；真实Float32 Scharr导数。") ]X,
    [Display(Name="Y方向一阶",Description="Y向下；真实Float32 Scharr导数。") ]Y
}
public sealed record ScharrOptions:DerivativeOptions{[DisplayName("一阶导数方向")]public ScharrDirection Direction{get;init;}}
public sealed record LaplacianOptions:DerivativeOptions
{
    [DisplayName("核大小（1/3/5/7）"),Range(1,7),Description("输出d²I/dx²+d²I/dy²，真实带符号Float32，不等于ConvertScaleAbs预览，也不宣称亚像素边缘。")]
    public int KernelSize{get;init;}=3;
}
public enum GradientAngleUnit
{
    [Display(Name="角度（度）",Description="顺时针为正，范围[0,360)。") ]Degrees,
    [Display(Name="弧度",Description="顺时针为正，范围[0,2π)。") ]Radians
}
public sealed record GradientMagnitudeDirectionOptions
{
    [DisplayName("方向单位")]
    [Description("图像X向右/Y向下，正方向顺时针；角度[0,360)或弧度[0,2π)。零梯度方向约定为0，并在DirectionValidMask中标为0，不作为有效测量。")]
    public GradientAngleUnit AngleUnit{get;init;}
}
public static class EdgeProcessors
{
    public static IReadOnlyList<string> Validate(DerivativeOptions p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var errors=new List<string>();if(!double.IsFinite(p.Scale)||Math.Abs(p.Scale)>1_000_000||!double.IsFinite(p.Delta)||Math.Abs(p.Delta)>1_000_000)errors.Add("scale/delta必须为±1000000内有限数值。");
        if(!Enum.IsDefined(p.Border)||p.MaximumPixels is <1 or >16_000_000)errors.Add("边界模式或像素预算无效（最多1600万像素）。");
        if(p is SobelOptions s)
        {
            if(s.KernelSize is not(1 or 3 or 5 or 7)||s.Dx<0||s.Dy<0||s.Dx+s.Dy==0)errors.Add("Sobel要求核1/3/5/7，非负导数阶数且至少一个方向大于0。");
            else if(s.KernelSize==1?(s.Dx>2||s.Dy>2||s.Dx>0&&s.Dy>0):(s.Dx>=s.KernelSize||s.Dy>=s.KernelSize))errors.Add("Sobel核1仅支持单方向一阶/二阶；其余每方向阶数必须小于核大小。");
        }
        if(p is ScharrOptions scharr&&!Enum.IsDefined(scharr.Direction))errors.Add("Scharr仅支持X或Y的一阶导数。");
        if(p is LaplacianOptions lap&&lap.KernelSize is not(1 or 3 or 5 or 7))errors.Add("Laplacian核大小必须为1/3/5/7。");return errors;
    }
    private static BorderTypes Border(EdgeBorder b)=>b switch{EdgeBorder.Reflect=>BorderTypes.Reflect,EdgeBorder.Replicate=>BorderTypes.Replicate,EdgeBorder.ConstantZero=>BorderTypes.Constant,_=>BorderTypes.Reflect101};
    public static (FloatPlaneFrame Derivative,ImageFrame Preview) Derivative(ImageFrame image,DerivativeOptions p,RotatedRectRoi? roi,CancellationToken ct)
    {
        ProcessingFrame.ThrowInvalid(Validate(p));InspectionFrames.Check(image,true);ct.ThrowIfCancellationRequested();using var view=RecognitionRegion.Open(image,roi,p.MaximumPixels);using var input=new OpenCvImageLease(view);using var builder=new FloatPlaneBuilder(view.Width,view.Height);using var raw=Mat.FromPixelData(view.Height,view.Width,MatType.CV_32FC1,builder.DataPointer,builder.Stride);string quantity;string unit;
        switch(p)
        {
            case SobelOptions s:Cv2.Sobel(input.Mat,raw,MatType.CV_32F,s.Dx,s.Dy,s.KernelSize,s.Scale,s.Delta,Border(s.Border)|BorderTypes.Isolated);quantity=s.Dx==1&&s.Dy==0?"GradientX":s.Dx==0&&s.Dy==1?"GradientY":$"Derivative({s.Dx},{s.Dy})";unit=$"intensity/pixel^{s.Dx+s.Dy}";break;
            case ScharrOptions s:Cv2.Scharr(input.Mat,raw,MatType.CV_32F,s.Direction==ScharrDirection.X?1:0,s.Direction==ScharrDirection.Y?1:0,s.Scale,s.Delta,Border(s.Border)|BorderTypes.Isolated);quantity=s.Direction==ScharrDirection.X?"GradientX":"GradientY";unit="intensity/pixel^1";break;
            case LaplacianOptions s:Cv2.Laplacian(input.Mat,raw,MatType.CV_32F,s.KernelSize,s.Scale,s.Delta,Border(s.Border)|BorderTypes.Isolated);quantity="Laplacian";unit="intensity/pixel^2";break;
            default:throw new ArgumentException("不支持的导数选项。");
        }
        ct.ThrowIfCancellationRequested();using var previewBuilder=new ImageFrameBuilder(view.Width,view.Height,PixelFormat.Mono8,coordinateSpace:view.CoordinateSpace);using(var preview=Mat.FromPixelData(view.Height,view.Width,MatType.CV_8UC1,previewBuilder.DataPointer,previewBuilder.Stride))Cv2.ConvertScaleAbs(raw,preview);
        ct.ThrowIfCancellationRequested();var result=builder.Build(view.FrameId,view.Timestamp,view.CoordinateSpace,quantity,unit);try{return(result,previewBuilder.Build(view.FrameId,view.Timestamp));}catch{result.Dispose();throw;}
    }
    public static (FloatPlaneFrame Magnitude,FloatPlaneFrame Direction,ImageFrame Preview,ImageFrame DirectionValidMask,int ZeroMagnitudeCount) MagnitudeDirection(FloatPlaneFrame x,FloatPlaneFrame y,GradientMagnitudeDirectionOptions p,CancellationToken ct)
    {
        if(!Enum.IsDefined(p.AngleUnit))throw new ArgumentException("方向单位无效。");if(x.Quantity!="GradientX"||y.Quantity!="GradientY")throw new ArgumentException("输入必须分别为一阶X/Y梯度；Laplacian或高阶导数不能冒充梯度。");
        if(x.Width!=y.Width||x.Height!=y.Height||x.FrameId!=y.FrameId||x.CoordinateSpace!=y.CoordinateSpace||x.Unit!=y.Unit)throw new ArgumentException("X/Y梯度的尺寸、帧、坐标空间与单位必须一致；不自动配对或重采样。");ct.ThrowIfCancellationRequested();
        using var lx=x.Lease();using var ly=y.Lease();using var magnitudeBuilder=new FloatPlaneBuilder(x.Width,x.Height);using var directionBuilder=new FloatPlaneBuilder(x.Width,x.Height);using var previewBuilder=new ImageFrameBuilder(x.Width,x.Height,PixelFormat.Mono8,coordinateSpace:x.CoordinateSpace);using var validBuilder=new ImageFrameBuilder(x.Width,x.Height,PixelFormat.Mono8,coordinateSpace:x.CoordinateSpace);var zero=0;
        for(var row=0;row<x.Height;row++)
        {
            ct.ThrowIfCancellationRequested();var xv=MemoryMarshal.Cast<byte,float>(lx.GetRowSpan(row));var yv=MemoryMarshal.Cast<byte,float>(ly.GetRowSpan(row));var m=magnitudeBuilder.GetRowSpan(row);var d=directionBuilder.GetRowSpan(row);var preview=previewBuilder.GetRowSpan(row);var valid=validBuilder.GetRowSpan(row);
            for(var column=0;column<x.Width;column++)
            {var a=xv[column];var b=yv[column];if(!float.IsFinite(a)||!float.IsFinite(b))throw new ArgumentException("梯度包含非有限数值。");var length=Math.Sqrt((double)a*a+(double)b*b);if(length>float.MaxValue)throw new ArgumentException("梯度幅值超出Float32范围。");m[column]=(float)length;preview[column]=(byte)Math.Clamp(Math.Round(length,MidpointRounding.ToEven),0,255);if(length==0){d[column]=0;valid[column]=0;zero++;}else{var angle=Math.Atan2(b,a);if(angle<0)angle+=Math.PI*2;var limit=p.AngleUnit==GradientAngleUnit.Degrees?360:Math.Tau;var value=(float)(p.AngleUnit==GradientAngleUnit.Degrees?angle*180/Math.PI:angle);d[column]=value>=limit?MathF.BitDecrement((float)limit):value;valid[column]=255;}}
        }
        var magnitude=magnitudeBuilder.Build(x.FrameId,x.Timestamp,x.CoordinateSpace,"GradientMagnitude",x.Unit);FloatPlaneFrame? direction=null;ImageFrame? output=null;
        try{direction=directionBuilder.Build(x.FrameId,x.Timestamp,x.CoordinateSpace,"GradientDirection",p.AngleUnit==GradientAngleUnit.Degrees?"degree":"radian");output=previewBuilder.Build(x.FrameId,x.Timestamp);return(magnitude,direction,output,validBuilder.Build(x.FrameId,x.Timestamp),zero);}catch{magnitude.Dispose();direction?.Dispose();output?.Dispose();throw;}
    }
}

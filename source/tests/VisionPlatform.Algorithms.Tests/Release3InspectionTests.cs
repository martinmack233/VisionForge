using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class Release3InspectionTests
{
    private static ImageFrame Gray(byte[] row, int height=1, Guid? id=null, ImageCoordinateSpace? space=null)
    {
        using var b=new ImageFrameBuilder(row.Length,height,PixelFormat.Mono8,row.Length+7,space);
        for(var y=0;y<height;y++)row.CopyTo(b.GetRowSpan(y));return b.Build(id);
    }
    private static byte[] Pixels(ImageFrame f) { using var l=f.Lease();return l.GetRowSpan(0).ToArray(); }
    private static ImageFrame Frame(Mat m, Guid? id=null, ImageCoordinateSpace? space=null)
    { using var b=new ImageFrameBuilder(m.Width,m.Height,m.Channels()==1?PixelFormat.Mono8:PixelFormat.BGR8,coordinateSpace:space);using var target=Mat.FromPixelData(m.Height,m.Width,m.Type(),b.DataPointer,b.Stride);m.CopyTo(target);return b.Build(id); }
    [Theory]
    [InlineData(ImageArithmeticOperation.AbsoluteDifference, 240, 30)]
    [InlineData(ImageArithmeticOperation.Add, 255, 70)]
    [InlineData(ImageArithmeticOperation.Subtract, 240, 0)]
    [InlineData(ImageArithmeticOperation.Weighted, 130, 35)]
    public void ArithmeticHasExplicit8BitRulesAndDoesNotMutateInputs(ImageArithmeticOperation op,int first,int second)
    {
        var id=Guid.NewGuid();using var a=Gray([250,20],2,id);using var b=Gray([10,50],2,id);
        using var output=InspectionProcessors.Arithmetic(a,b,new(){Operation=op,Alpha=.5,Beta=.5},null,default);
        Assert.Equal(new byte[]{(byte)first,(byte)second},Pixels(output));Assert.Equal(new byte[]{250,20},Pixels(a));Assert.Equal(id,output.FrameId);Assert.NotSame(a.BufferHandle,output.BufferHandle);
    }
    [Fact] public void ArithmeticRequiresExplicitReferenceFrameAndMatchingSpaceDimensionsFormat()
    {
        using var a=Gray([1,2]);using var b=Gray([1,2]);
        Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,b,new(),null,default));
        using var ok=InspectionProcessors.Arithmetic(a,b,new(){AllowDifferentFrames=true},null,default);
        using var shifted=Gray([1,2],1,a.FrameId,new("image",1,0));
        Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,shifted,new(),null,default));
        using var wrong=Gray([1],1,a.FrameId);Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,wrong,new(),null,default));
        using var rgb=new ImageFrameBuilder(2,1,PixelFormat.BGR8).Build(a.FrameId);Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,rgb,new(),null,default));
        Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,a,new(){Alpha=double.NaN},null,default));
    }
    [Fact] public void MaskStatisticsAndRoiAreRealAndEmptyMaskIsUndefined()
    {
        var id=Guid.NewGuid();using var a=Gray([0,10,20,30],3,id,new("image",100,200));using var mask=Gray([0,255,255,0],3,id,new("image",100,200));
        var result=InspectionProcessors.Statistics(a,mask,null,default);Assert.Equal(6,result.PixelCount);Assert.Equal(15,result.Mean);Assert.Equal(5,result.StandardDeviation);Assert.Equal(1,result.ForegroundRatio);
        using var masked=InspectionProcessors.Mask(a,mask,null,default);Assert.Equal(new byte[]{0,10,20,0},Pixels(masked));
        using var roi=InspectionProcessors.Arithmetic(a,a,new(),new(102,201.5,2,3),default);Assert.Equal(2,roi.Width);Assert.Equal(101,roi.CoordinateSpace.OriginX);
        using var empty=Gray([0,0,0,0],3,id,new("image",100,200));var missing=InspectionProcessors.Statistics(a,empty,null,default);Assert.False(missing.Valid);Assert.Null(missing.Mean);Assert.Null(missing.ForegroundRatio);
        Assert.Throws<ArgumentException>(()=>InspectionProcessors.Arithmetic(a,a,new(),new(102,201,2,2,30),default));
        Assert.ThrowsAny<ArgumentException>(()=>InspectionProcessors.Statistics(a,null,new(0,0,2,2),default));
    }
    [Theory] [InlineData(WorkingColorSpace.Hsv8)] [InlineData(WorkingColorSpace.Lab8)]
    public void WorkingColorIsTypedAndHasExplicitPreview(WorkingColorSpace space)
    {
        using var m=new Mat(2,3,MatType.CV_8UC3);m.Set(0,0,new Vec3b(0,0,255));m.Set(0,1,new Vec3b(0,255,0));m.Set(0,2,new Vec3b(255,0,0));using(var first=m.Row(0))using(var second=m.Row(1))first.CopyTo(second);using var image=Frame(m);
        var converted=ColorProcessors.Convert(image,new(){ColorSpace=space},default);using var color=converted.Color;using var preview=converted.Preview;
        Assert.Equal(image.FrameId,color.FrameId);Assert.Equal(PixelFormat.BGR8,preview.PixelFormat);Assert.NotSame(image.BufferHandle,color.BufferHandle);
        using var channel=ColorProcessors.Channel(color,0,default);using var expected=new Mat();Cv2.CvtColor(m,expected,space==WorkingColorSpace.Hsv8?ColorConversionCodes.BGR2HSV:ColorConversionCodes.BGR2Lab);Assert.Equal(expected.At<Vec3b>(0,1).Item0,Pixels(channel)[1]);
        if(space==WorkingColorSpace.Hsv8){using var mask=ColorProcessors.Range(color,new(){Lower0=40,Upper0=80,Lower1=200,Lower2=200},default);Assert.Equal(new byte[]{0,255,0},Pixels(mask));Assert.Throws<ArgumentException>(()=>ColorProcessors.Range(color,new(){Upper0=180},default));}
        Assert.Throws<ArgumentException>(()=>ColorProcessors.Channel(color,3,default));Assert.Equal(new byte[]{0,0,255,0,255,0,255,0,0},Pixels(image));
    }
    [Fact] public void ColorPortsRejectOrdinaryImageAlgorithmsAndDisposeOwnedChannels()
    {
        var registry=BuiltinRegistration.CreateRegistry();var descriptor=registry.Create("ColorSpaceConvert").Descriptor;
        Assert.Equal(typeof(ColorPlaneFrame),descriptor.OutputPorts.Single(p=>p.Name=="ColorData").DataType);
        using var mono=Gray([0,0]);Assert.Throws<ArgumentException>(()=>ColorProcessors.Convert(mono,new(),default));
        using var bayer=new ImageFrameBuilder(2,2,PixelFormat.BayerRG8).Build();Assert.Throws<ArgumentException>(()=>ColorProcessors.Convert(bayer,new(),default));
    }
    [Fact] public async Task AllContoursHolesMetricsAndSourceLinksSurviveOperations()
    {
        using var m=new Mat(160,220,MatType.CV_8UC1,Scalar.All(0));Cv2.Rectangle(m,new Rect(10,10,61,41),Scalar.All(255),-1);Cv2.Circle(m,new Point(150,70),35,Scalar.All(255),-1);Cv2.Circle(m,new Point(150,70),12,Scalar.All(0),-1);
        using var image=Frame(m,space:new("image",100,200));var run=Guid.NewGuid();var contours=ShapeProcessors.Contours(image,new(){Retrieval=ContourRetrieval.Tree,Chain=ContourChain.None},null,run,"find",default);Assert.Equal(3,contours.Count);Assert.Single(contours.Items.Cast<ContourItem>(),c=>c.IsHole==true);
        var analyzed=ShapeProcessors.Analyze(contours,new(),default);Assert.Equal(3,analyzed.Count);Assert.Equal(contours.Items.Select(i=>i.Origin.Key),analyzed.Items.Select(i=>i.Origin.Key));
        var rectangle=analyzed.Items.Cast<ContourItem>().Single(c=>Math.Abs(c.Analysis!.Area!.Value-2400)<.1);Assert.Equal(200,rectangle.Analysis!.Perimeter!.Value,5);Assert.Equal(1,rectangle.Analysis.Rectangularity!.Value,4);Assert.Equal(140,rectangle.Region!.CenterX,3);Assert.Equal(230,rectangle.Region.CenterY,3);Assert.Equal("image",rectangle.Region.CoordinateSpaceId);
        Assert.All(analyzed.Items.Cast<ContourItem>(),c=>{Assert.True(c.Analysis!.Circularity>0);Assert.NotEmpty(c.Analysis.ConvexHull);Assert.NotEmpty(c.Analysis.Approximation);Assert.Equal(image.FrameId,c.Origin.FrameId);});
        using var filtered=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("f","CollectionFilter",new{Field="Area",Number=500d}),new Dictionary<string,object>{{"Collection",analyzed}});Assert.True(filtered.Success,filtered.ErrorMessage);Assert.Equal(2,filtered.Nodes["f"].Output<VisionCollection>("Collection").Count);
        var external=ShapeProcessors.Contours(image,new(){Retrieval=ContourRetrieval.External},null,run,"external",default);Assert.Equal(2,external.Count);
        Assert.Throws<ArgumentException>(()=>ShapeProcessors.Contours(image,new(){MaximumContours=1},null,run,"f",default));
    }
    [Fact] public void RotatedRectangleAndEllipseKeepAnglesAndDegenerateMetricsAreAbsent()
    {
        var source=new ItemOrigin(Guid.NewGuid(),"source",0,null,"image","key");var rr=new RotatedRect(new(80,70),new(60,30),27);
        var ellipse=Enumerable.Range(0,72).Select(i=>{var t=i*Math.PI/36;return new Point2D(120+40*Math.Cos(t),160+20*Math.Sin(t));}).ToArray();
        var collection=new VisionCollection(VisionItemKind.Contour,[new ContourItem(source,rr.Points().Select(p=>new Point2D(p.X,p.Y)).ToArray(),true),new ContourItem(source with{SourceIndex=1,Key="ellipse"},ellipse,true),new ContourItem(source with{Key="degenerate"},[new(0,0),new(10,0),new(20,0)],true)]);
        var result=ShapeProcessors.Analyze(collection,new(),default);var r=(ContourItem)result.Items[0];Assert.InRange(r.Region!.AngleDegrees,26.9,27.1);var e=((ContourItem)result.Items[1]).Analysis!.Ellipse!;Assert.InRange(Math.Min(e.Width,e.Height),39.9,40.1);Assert.InRange(Math.Max(e.Width,e.Height),79.9,80.1);
        var bad=(ContourItem)result.Items[2];Assert.Null(bad.Analysis!.Area);Assert.False(bad.Fields.ContainsKey("Circularity"));Assert.Equal(BusinessState.Ng,bad.State);Assert.Equal("key",result.Items[0].Origin.Key);
    }
    [Fact] public void EmptyInvalidAndCancelledContoursNeverProduceFakeGeometry()
    {
        using var black=Gray(new byte[20],20);Assert.Empty(ShapeProcessors.Contours(black,new(),null,Guid.NewGuid(),"f",default).Items);
        using var gray=Gray([12,100],2);Assert.Throws<ArgumentException>(()=>ShapeProcessors.Contours(gray,new(),null,Guid.NewGuid(),"f",default));
        Assert.Throws<ArgumentException>(()=>ShapeProcessors.Lines(gray,new(),null,Guid.NewGuid(),"f",default));
        Assert.Throws<OperationCanceledException>(()=>ShapeProcessors.Contours(black,new(),null,Guid.NewGuid(),"f",new(true)));
        Assert.Throws<ArgumentException>(()=>ShapeProcessors.Circles(black,new(){MinimumRadius=100,MaximumRadius=5},null,Guid.NewGuid(),"f",default));
    }
    [Fact] public async Task RealHoughCollectionsConnectToExistingSequentialMeasurement()
    {
        using var edges=new Mat(240,320,MatType.CV_8UC1,Scalar.All(0));Cv2.Line(edges,new(20,40),new(280,40),Scalar.All(255));Cv2.Line(edges,new(30,180),new(200,100),Scalar.All(255));using var image=Frame(edges,space:new("image",1000,2000));
        var lines=ShapeProcessors.Lines(image,new(){Threshold=30,MinimumLength=60,MaximumGap=4},null,Guid.NewGuid(),"hough",default);Assert.True(lines.Count>=2);Assert.All(lines.Items.Cast<LineItem>(),l=>{Assert.NotNull(l.EndPoint);Assert.True(l.Fields["Length"].Number>=60);Assert.True(l.Line.Origin.X>=1000);});
        var body=new SubflowDefinition{Nodes=[CollectionSubflows.N("current","IterationInput",new{}),CollectionSubflows.N("geometry","ItemGeometry",new{}),CollectionSubflows.N("measure","DistanceMeasure",new{}),CollectionSubflows.N("collect","IterationOutput",new{})],Connections=[new("current","Item","geometry","Item"),new("geometry","PointA","measure","PointA"),new("geometry","PointB","measure","PointB"),new("measure","Result","collect","Measurement"),new("measure","Distance","collect","Number")]};
        using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("each","ForEach",new ForEachOptions{Body=body}),new Dictionary<string,object>{{"Collection",lines}});Assert.True(run.Success,run.ErrorMessage);var batch=run.Nodes["each"].Output<IterationBatch>("Batch");Assert.Equal(lines.Count,batch.CompletedCount);Assert.All(batch.Results.Items.Cast<IterationItem>(),i=>Assert.Equal(i.SourceItem.Fields["Length"].Number,i.Result!.Number));
        using var circlesImage=new Mat(240,320,MatType.CV_8UC1,Scalar.All(0));Cv2.Circle(circlesImage,new Point(160,120),45,Scalar.All(255),2);using var circleFrame=Frame(circlesImage);var circles=ShapeProcessors.Circles(circleFrame,new(){MinimumRadius=40,MaximumRadius=50,CenterThreshold=18,MinimumDistance=80},null,Guid.NewGuid(),"c",default);Assert.NotEmpty(circles.Items);Assert.Contains(circles.Items.Cast<CircleItem>(),c=>Math.Abs(c.Circle.Center.X-160)<3&&Math.Abs(c.Circle.Center.Y-120)<3&&Math.Abs(c.Circle.Radius-45)<3);
    }
    [Theory] [InlineData(PixelFormat.Mono8)] [InlineData(PixelFormat.RGB8)] [InlineData(PixelFormat.BGR8)]
    public void ControlledTiffPreservesSamplesWithPaddedStrideAndViews(PixelFormat format)
    {
        var path=Path.Combine(Path.GetTempPath(),"VisionStudio-release3-test-"+Guid.NewGuid().ToString("N")+".tif");
        try
        {
            using var builder=new ImageFrameBuilder(5,4,format,stride:5*format.BytesPerPixel()+9);
            for(var y=0;y<4;y++)for(var x=0;x<builder.GetRowSpan(y).Length;x++)builder.GetRowSpan(y)[x]=(byte)(x+y*10);
            using var image=builder.Build();using var view=image.CreateView(1,1,3,2);
            VisionPlatform.Algorithms.AI.ControlledTiffChannel.Write(view,path,default);
            using var decoded=Cv2.ImRead(path,ImreadModes.Unchanged);Assert.Equal(3,decoded.Width);Assert.Equal(2,decoded.Height);Assert.Equal(160+3*2*format.BytesPerPixel(),new FileInfo(path).Length);
            using var lease=view.Lease();var expected=lease.GetRowSpan(0).ToArray();var actual=new byte[expected.Length];Marshal.Copy(decoded.Data,actual,0,actual.Length);
            if(format==PixelFormat.RGB8)for(var x=0;x<actual.Length;x+=3)(actual[x],actual[x+2])=(actual[x+2],actual[x]);
            Assert.Equal(expected,actual);Assert.Throws<IOException>(()=>VisionPlatform.Algorithms.AI.ControlledTiffChannel.Write(view,path,default));
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
    [Fact] public async Task ColorBuffersParticipateInSubflowBudgetAndRelease()
    {
        using var builder=new ImageFrameBuilder(800,600,PixelFormat.BGR8);using var image=builder.Build();var handle=image.BufferHandle;
        var origin=new ItemOrigin(Guid.NewGuid(),"roi",0,image.FrameId,"image","origin");var rois=new VisionCollection(VisionItemKind.Roi,[new RoiItem(origin,new(400,300,800,600))]);
        var body=new SubflowDefinition{Nodes=[CollectionSubflows.N("current","IterationInput",new{}),CollectionSubflows.N("color","ColorSpaceConvert",new{}),CollectionSubflows.N("range","ColorInRange",new{}),CollectionSubflows.N("stats","RegionStatistics",new{}),CollectionSubflows.N("collect","IterationOutput",new{})],Connections=[new("current","Image","color","Image"),new("color","ColorData","range","ColorData"),new("range","BinaryImage","stats","Image"),new("stats","Mean","collect","Number")]};
        using(var failed=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("each","ForEach",new ForEachOptions{Body=body,WorkingImageMegabytes=2}),new Dictionary<string,object>{{"Collection",rois},{"Image",image}}))
        {Assert.False(failed.Success);Assert.Contains("budget",failed.ErrorMessage);}
        using(var success=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("each","ForEach",new ForEachOptions{Body=body,WorkingImageMegabytes=16,DebugImageMegabytes=2}),new Dictionary<string,object>{{"Collection",rois},{"Image",image}}))
        {Assert.True(success.Success,success.ErrorMessage);Assert.Equal(1,success.Nodes["each"].Output<int>("CompletedCount"));Assert.True(success.Nodes["each"].Output<IterationTraceStore>("Debug").RetainedBytes<=2*1024*1024);}
        Assert.Equal(1,handle.ReferenceCount);
    }

}

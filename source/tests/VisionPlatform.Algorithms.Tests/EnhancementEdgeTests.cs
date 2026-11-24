using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Collections;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
[CollectionDefinition("EnhancementEdgeResources",DisableParallelization=true)]public sealed class EnhancementEdgeResources;
[Collection("EnhancementEdgeResources")]
public sealed class EnhancementEdgeTests
{
    private static ImageFrame Gray(byte[] row,int height=5,ImageCoordinateSpace? space=null)
    {using var b=new ImageFrameBuilder(row.Length,height,PixelFormat.Mono8,row.Length+7,space);for(var y=0;y<height;y++)row.CopyTo(b.GetRowSpan(y));return b.Build();}
    private static byte[] Pixels(ImageFrame f,int row=0){using var l=f.Lease();return l.GetRowSpan(row).ToArray();}
    private static float[] Numbers(FloatPlaneFrame f,int row=0){using var l=f.Lease();return MemoryMarshal.Cast<byte,float>(l.GetRowSpan(row)).ToArray();}
    private static void Dispose((FloatPlaneFrame Derivative,ImageFrame Preview) r){r.Derivative.Dispose();r.Preview.Dispose();}
    [Fact]public void NumericConstraintAcceptsTheSameTypesInGraphAndRuntimeButNeverStringsOrBoolean()
    {var input=new PortDefinition("Number",typeof(double)){Constraint=PortValueConstraint.Numeric};foreach(var value in new object[]{(byte)2,3,4L,5f,6d,7m}){Assert.True(NodePorts.Compatible(new("Value",value.GetType()),input));Assert.True(NodePorts.Accepts(input,value));}Assert.False(NodePorts.Accepts(input,"3"));Assert.False(NodePorts.Accepts(input,true));Assert.False(NodePorts.Accepts(new("Image",typeof(ImageFrame)),3));}
    [Fact]public async Task SequentialRoiSubflowRetainsBoundedSignedPlanesAndReleasesAllItsShares()
    {
        var bytes=FrameHandle.ActiveBytes;using(var image=Gray([0,30,200,30,0],8))
        {
            var inputBytes=FrameHandle.ActiveBytes;NodeSpecification N(string id,string type,object p)=>new(id,type,id,JsonDefaults.Element(p));
            var body=new SubflowDefinition{Nodes=[N("current","IterationInput",new{}),N("roi","ItemToRoi",new ItemRoiOptions()),N("x","Sobel",new SobelOptions{Dx=1,Dy=0}),N("y","Sobel",new SobelOptions{Dx=0,Dy=1}),N("magnitude","GradientMagnitudeDirection",new GradientMagnitudeDirectionOptions()),N("collect","IterationOutput",new{})],Connections=[new("current","Item","roi","Item"),new("current","Image","roi","TargetImage"),new("current","Image","x","Image"),new("current","Image","y","Image"),new("roi","ROI","x","ROI"),new("roi","ROI","y","ROI"),new("x","Derivative","magnitude","GradientX"),new("y","Derivative","magnitude","GradientY"),new("magnitude","ZeroMagnitudeCount","collect","Number")]};
            var collection=new VisionCollection(VisionItemKind.Roi,Enumerable.Range(0,2).Select(i=>new RoiItem(new(Guid.NewGuid(),"source",i,image.FrameId,"image","roi-"+i),new(2.5,2+i*3,3,3))));
            using(var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(N("each","ForEach",new ForEachOptions{Body=body,DebugIterations=1,DebugImageMegabytes=1}),new Dictionary<string,object>{{"Collection",collection},{"Image",image}}))
            {Assert.True(run.Success,run.ErrorMessage);var batch=run.Nodes["each"].Output<IterationBatch>("Batch");Assert.Equal(2,batch.CompletedCount);Assert.Equal(new[]{"roi-0","roi-1"},batch.Results.Items.Select(i=>i.Origin.Key));var traces=run.Nodes["each"].Output<IterationTraceStore>("Debug");var trace=Assert.Single(traces.Traces);var plane=trace.Nodes.Single(n=>n.NodeId=="x").Output<FloatPlaneFrame>("Derivative");Assert.Equal(3,plane.Width);Assert.Equal(image.FrameId,plane.FrameId);Assert.InRange(traces.RetainedBytes,1,1048576);Assert.Equal(3,Numbers(plane).Length);}
            Assert.Equal(inputBytes,FrameHandle.ActiveBytes);
            var invalid=body with{Connections=body.Connections.Where(e=>e.TargetPort!="ROI").ToList()};Assert.Contains(SubflowValidation.Validate(BuiltinRegistration.CreateRegistry(),invalid),e=>e.Contains("explicit ROI"));
        }
        Assert.Equal(bytes,FrameHandle.ActiveBytes);
    }
    [Fact]public void LinearRangeGammaAndInvertMatchKnownValuesWithoutChangingInput()
    {
        using var input=Gray([0,10,100,255]);var linear=EnhancementProcessors.Apply(input,new LinearContrastOptions{Alpha=2,Beta=-20},null,default);using(linear.Image){Assert.Equal(new byte[]{0,0,180,255},Pixels(linear.Image));Assert.Equal(108.75,linear.Statistics.Channels[0].OutputMean);Assert.Equal(input.FrameId,linear.Statistics.FrameId);}
        var range=EnhancementProcessors.Apply(input,new RangeStretchOptions{InputMinimum=10,InputMaximum=100,OutputMinimum=20,OutputMaximum=200},null,default);using(range.Image)Assert.Equal(new byte[]{20,20,200,200},Pixels(range.Image));
        using var gammaInput=Gray([0,64,255]);var gamma=EnhancementProcessors.Apply(gammaInput,new GammaCorrectionOptions{Gamma=2},null,default);using(gamma.Image)Assert.Equal(new byte[]{0,16,255},Pixels(gamma.Image));var brighter=EnhancementProcessors.Apply(gammaInput,new GammaCorrectionOptions{Gamma=.5},null,default);using(brighter.Image)Assert.Equal(new byte[]{0,128,255},Pixels(brighter.Image));
        var invert=EnhancementProcessors.Apply(input,new EnhancementBudgetOptions(),null,default);using(invert.Image)Assert.Equal(new byte[]{255,245,155,0},Pixels(invert.Image));Assert.Equal(new byte[]{0,10,100,255},Pixels(input));
    }
    [Fact]public void PercentilesUseEveryPixelAndKeepActualSelectedLimits()
    {
        using var image=Gray([0,100,100,100,105,105,110,110,110,255],1);var p=new PercentileStretchOptions{LowPercentile=20,HighPercentile=80};var r=EnhancementProcessors.Apply(image,p,null,default);using(r.Image){Assert.Equal(100,r.Statistics.SelectedInputMinimum);Assert.Equal(110,r.Statistics.SelectedInputMaximum);Assert.Equal(new byte[]{0,0,0,0,128,128,255,255,255,255},Pixels(r.Image));Assert.False(r.Statistics.ConstantRange);}
        using var constant=Gray([99,99,99],1);var flat=EnhancementProcessors.Apply(constant,new PercentileStretchOptions{OutputMinimum=12,OutputMaximum=240},null,default);using(flat.Image){Assert.True(flat.Statistics.ConstantRange);Assert.Equal(new byte[]{12,12,12},Pixels(flat.Image));Assert.Equal(0,flat.Statistics.Channels[0].OutputStandardDeviation);}
    }
    [Theory][InlineData(PixelFormat.RGB8)][InlineData(PixelFormat.BGR8)]public void ColorUsesSameMappingAndNeverChangesPixelFormat(PixelFormat format)
    {
        using var b=new ImageFrameBuilder(2,2,format,11);for(var y=0;y<2;y++)new byte[]{10,50,100,20,80,200}.CopyTo(b.GetRowSpan(y));using var image=b.Build();var r=EnhancementProcessors.Apply(image,new LinearContrastOptions{Alpha=2},null,default);using(r.Image){Assert.Equal(format,r.Image.PixelFormat);Assert.Equal(new byte[]{20,100,200,40,160,255},Pixels(r.Image));Assert.Equal(3,r.Statistics.Channels.Count);}
        Assert.Throws<ArgumentException>(()=>EnhancementProcessors.Apply(image,new PercentileStretchOptions(),null,default));Assert.Throws<NotSupportedException>(()=>new EqualizeHistProcessor().Process(image,new()));Assert.Throws<NotSupportedException>(()=>new ClaheProcessor().Process(image,new()));
    }
    [Fact]public void RoiStatisticsAndCoordinatesReferOnlyToProcessedPixels()
    {
        using var source=Gray([0,20,40,60,255],5,new("part",100,200));var r=EnhancementProcessors.Apply(source,new LinearContrastOptions{Alpha=2},new(102.5,202.5,3,3,0,"part"),default);using(r.Image){Assert.Equal(3,r.Image.Width);Assert.Equal(new("part",101,201),r.Image.CoordinateSpace);Assert.Equal(new byte[]{40,80,120},Pixels(r.Image));Assert.Equal(40,r.Statistics.Channels[0].InputMean);Assert.Equal(80,r.Statistics.Channels[0].OutputMean);Assert.Equal(3,r.Statistics.Width);}
        Assert.Throws<ArgumentException>(()=>EnhancementProcessors.Apply(source,new LinearContrastOptions(),new(102,202,2,2,20,"part"),default));Assert.Throws<ArgumentException>(()=>EnhancementProcessors.Apply(source,new LinearContrastOptions(),new(2,2,2,2),default));
    }
    [Theory][InlineData("range")][InlineData("percentile")][InlineData("gamma")][InlineData("linear")][InlineData("budget")]
    public void InvalidEnhancementParametersFailBeforeAllocating(string kind)
    {
        EnhancementBudgetOptions p=kind switch{"range"=>new RangeStretchOptions{InputMinimum=20,InputMaximum=20},"percentile"=>new PercentileStretchOptions{LowPercentile=99,HighPercentile=2},"gamma"=>new GammaCorrectionOptions{Gamma=0},"linear"=>new LinearContrastOptions{Alpha=double.NaN},_=>new EnhancementBudgetOptions{MaximumPixels=0}};
        using var input=Gray([0,100,255]);var before=FrameHandle.ActiveBytes;Assert.NotEmpty(EnhancementProcessors.Validate(p));Assert.Throws<ArgumentException>(()=>EnhancementProcessors.Apply(input,p,null,default));Assert.Equal(before,FrameHandle.ActiveBytes);
    }
    [Fact]public void BayerAndCancellationAreExplicitAndReleaseBorrowedReferences()
    {
        using var bayer=new ImageFrameBuilder(4,4,PixelFormat.BayerRG8).Build();Assert.Throws<ArgumentException>(()=>EnhancementProcessors.Apply(bayer,new GammaCorrectionOptions(),null,default));Assert.Throws<ArgumentException>(()=>EdgeProcessors.Derivative(bayer,new SobelOptions(),null,default));
        using var image=Gray([0,10,20]);using var cancel=new CancellationTokenSource();cancel.Cancel();var bytes=FrameHandle.ActiveBytes;Assert.Throws<OperationCanceledException>(()=>EnhancementProcessors.Apply(image,new LinearContrastOptions(),null,cancel.Token));Assert.Throws<OperationCanceledException>(()=>EdgeProcessors.Derivative(image,new SobelOptions(),null,cancel.Token));Assert.Equal(bytes,FrameHandle.ActiveBytes);Assert.Equal(1,image.BufferHandle.ReferenceCount);
    }
    [Fact]public void SignedSobelPreservesNegativeAndFractionalRawValuesWithSeparatePreview()
    {
        using var image=Gray([0,10,20,30,40,30,20,10,0]);var r=EdgeProcessors.Derivative(image,new SobelOptions{Scale=.25,Delta=.125},null,default);try{Assert.Equal(20.125f,Numbers(r.Derivative,2)[2]);Assert.Equal(-19.875f,Numbers(r.Derivative,2)[6]);Assert.Equal((byte)20,Pixels(r.Preview,2)[6]);Assert.Equal("GradientX",r.Derivative.Quantity);Assert.Equal(image.FrameId,r.Derivative.FrameId);Assert.NotSame(image.BufferHandle,r.Derivative.BufferHandle);}finally{Dispose(r);}
        Assert.Equal(new byte[]{0,10,20,30,40,30,20,10,0},Pixels(image));
    }
    [Fact]public void ScharrAndLaplacianPerformTheirRealKernels()
    {
        using var ramp=Gray([0,10,20,30,40]);var scharr=EdgeProcessors.Derivative(ramp,new ScharrOptions(),null,default);try{Assert.Equal(320f,Numbers(scharr.Derivative,2)[2]);Assert.Equal((byte)255,Pixels(scharr.Preview,2)[2]);}finally{Dispose(scharr);}
        using var spike=Gray([0,0,10,0,0]);var lap=EdgeProcessors.Derivative(spike,new LaplacianOptions{KernelSize=1},null,default);try{Assert.Equal(-20f,Numbers(lap.Derivative,2)[2]);Assert.Equal(10f,Numbers(lap.Derivative,2)[1]);Assert.Equal("Laplacian",lap.Derivative.Quantity);}finally{Dispose(lap);}
    }
    [Fact]public void MagnitudeDirectionHasImageAxisConventionAndZeroValidityMask()
    {
        using var b=new ImageFrameBuilder(7,7,PixelFormat.Mono8);for(var y=0;y<7;y++)for(var x=0;x<7;x++)b.GetRowSpan(y)[x]=(byte)(10*x+20*y);using var image=b.Build();var xg=EdgeProcessors.Derivative(image,new SobelOptions(),null,default);var yg=EdgeProcessors.Derivative(image,new SobelOptions{Dx=0,Dy=1},null,default);
        try{var r=EdgeProcessors.MagnitudeDirection(xg.Derivative,yg.Derivative,new(),default);using(r.Magnitude)using(r.Direction)using(r.Preview)using(r.DirectionValidMask){Assert.InRange(Numbers(r.Magnitude,3)[3],178.885f,178.887f);Assert.InRange(Numbers(r.Direction,3)[3],63.434f,63.436f);Assert.Equal((byte)255,Pixels(r.DirectionValidMask,3)[3]);Assert.Equal((byte)0,Pixels(r.DirectionValidMask)[0]);Assert.True(r.ZeroMagnitudeCount>0);Assert.Equal("degree",r.Direction.Unit);}
            var radians=EdgeProcessors.MagnitudeDirection(xg.Derivative,yg.Derivative,new(){AngleUnit=GradientAngleUnit.Radians},default);using(radians.Magnitude)using(radians.Direction)using(radians.Preview)using(radians.DirectionValidMask)Assert.InRange(Numbers(radians.Direction,3)[3],1.1071f,1.1072f);
            Assert.Throws<ArgumentException>(()=>EdgeProcessors.MagnitudeDirection(yg.Derivative,xg.Derivative,new(),default));
        }finally{Dispose(xg);Dispose(yg);}
    }
    [Theory][InlineData(0,0,3)][InlineData(1,0,2)][InlineData(3,0,3)][InlineData(1,1,1)][InlineData(0,3,1)]public void SobelIllegalOrderAndKernelAreRejected(int dx,int dy,int size)
    {using var image=Gray([0,1,2,3]);var options=new SobelOptions{Dx=dx,Dy=dy,KernelSize=size};Assert.NotEmpty(EdgeProcessors.Validate(options));Assert.Throws<ArgumentException>(()=>EdgeProcessors.Derivative(image,options,null,default));}
    [Fact]public void RoiDerivativeBorderIsIsolatedAndLargeSourceIsNotCloned()
    {
        using var image=Gray([0,10,20,30,40,50,60],5,new("image",100,200));var full=EdgeProcessors.Derivative(image,new SobelOptions(),null,default);var roi=EdgeProcessors.Derivative(image,new SobelOptions(),new(103.5,202.5,3,3),default);try{Assert.Equal(80f,Numbers(full.Derivative,2)[2]);Assert.Equal(0f,Numbers(roi.Derivative,1)[0]);Assert.Equal(new("image",102,201),roi.Derivative.CoordinateSpace);}finally{Dispose(full);Dispose(roi);}
        var baseline=FrameHandle.ActiveBytes;using(var builder=new ImageFrameBuilder(8192,10000,PixelFormat.Mono8)){for(var y=100;y<140;y++)builder.GetRowSpan(y).Slice(100,40).Fill(100);using var large=builder.Build();var result=EdgeProcessors.Derivative(large,new SobelOptions{MaximumPixels=10000},new(120,120,40,40),default);try{Assert.Equal(6400,result.Derivative.BufferHandle.Length);Assert.Equal(1600,result.Preview.BufferHandle.Length);Assert.Equal(baseline+81920000+8000,FrameHandle.ActiveBytes);}finally{Dispose(result);}}Assert.Equal(baseline,FrameHandle.ActiveBytes);
    }
    [Fact]public async Task ExistingEnhancementNodesKeepOldOutputsAndAddRoiAndActualStatistics()
    {
        using var image=Gray([0,20,40,60,255],5);var registry=BuiltinRegistration.CreateRegistry();var runtime=new RuntimeExecutor(registry);
        foreach(var id in new[]{"Normalize","EqualizeHist","CLAHE"})
        {
            var p=id=="Normalize"?JsonDefaults.Element(new NormalizeOptions()):id=="CLAHE"?JsonDefaults.Element(new ClaheOptions()):JsonDefaults.Element(new EqualizeHistOptions());
            using var run=await runtime.ExecuteSingleAsync(new(id,id,id,p),new Dictionary<string,object>{{"Image",image},{"ROI",new RectRoi(1,1,3,3)}});Assert.True(run.Success,run.ErrorMessage);var output=run.Nodes[id].Output<ImageFrame>("ProcessedImage");var stats=run.Nodes[id].Output<EnhancementStatistics>("Statistics");Assert.Equal(3,output.Width);Assert.Equal(40,stats.Channels[0].InputMean);Assert.Equal(3,stats.Width);
        }
    }
    [Fact]public async Task NewPortsAndNodeResourcesUseExistingRuntime()
    {
        using var image=Gray([0,20,40,20,0]);var runtime=new RuntimeExecutor(BuiltinRegistration.CreateRegistry());var bytes=FrameHandle.ActiveBytes;FloatPlaneFrame derivative;ImageFrame preview;
        using(var run=await runtime.ExecuteSingleAsync(new("sobel","Sobel","Sobel",JsonDefaults.Element(new SobelOptions())),new Dictionary<string,object>{{"Image",image}})){Assert.True(run.Success,run.ErrorMessage);derivative=run.Nodes["sobel"].Output<FloatPlaneFrame>("Derivative");preview=run.Nodes["sobel"].Output<ImageFrame>("Preview");Assert.NotEqual(typeof(ImageFrame),derivative.GetType());}
        Assert.Throws<ObjectDisposedException>(()=>derivative.Lease());Assert.True(preview.IsDisposed);Assert.Equal(bytes,FrameHandle.ActiveBytes);
    }
    [Fact]public void GradientIdentityMismatchAndNonFiniteValuesCannotBecomeValidDirections()
    {
        using var b=new FloatPlaneBuilder(2,2);for(var y=0;y<2;y++)b.GetRowSpan(y).Fill(1);using var x=b.Build(Guid.NewGuid(),DateTimeOffset.Now,new(),"GradientX","intensity/pixel^1");
        using var other=new FloatPlaneBuilder(2,2);for(var y=0;y<2;y++)other.GetRowSpan(y).Fill(2);using var wrong=other.Build(Guid.NewGuid(),DateTimeOffset.Now,new(),"GradientY","intensity/pixel^1");
        var bytes=FrameHandle.ActiveBytes;Assert.Throws<ArgumentException>(()=>EdgeProcessors.MagnitudeDirection(x,wrong,new(),default));Assert.Equal(bytes,FrameHandle.ActiveBytes);
        using var bad=new FloatPlaneBuilder(2,2);for(var y=0;y<2;y++)bad.GetRowSpan(y).Fill(float.NaN);using var invalid=bad.Build(x.FrameId,x.Timestamp,x.CoordinateSpace,"GradientY",x.Unit);bytes=FrameHandle.ActiveBytes;
        Assert.Throws<ArgumentException>(()=>EdgeProcessors.MagnitudeDirection(x,invalid,new(),default));Assert.Equal(bytes,FrameHandle.ActiveBytes);Assert.Equal(1,x.BufferHandle.ReferenceCount);Assert.Equal(1,invalid.BufferHandle.ReferenceCount);
    }
    [Fact]public async Task ExistingCannyMatchesOpenCvWithEquivalentParametersAndRejectsWrongFormat()
    {
        using var image=Gray([0,0,0,80,80,80,0,0,0],9);var p=new CannyOptions{Low=20,High=50,Aperture=5,L2Gradient=true};using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(new("edge","Canny","Canny",JsonDefaults.Element(p)),new Dictionary<string,object>{{"Image",image}});Assert.True(run.Success,run.ErrorMessage);var actual=run.Nodes["edge"].Output<ImageFrame>("Edges");using var lease=image.Lease();using var input=Mat.FromPixelData(image.Height,image.Width,MatType.CV_8UC1,lease.DataPointer,lease.Stride);using var expected=new Mat();Cv2.Canny(input,expected,p.Low,p.High,p.Aperture,p.L2Gradient);for(var row=0;row<image.Height;row++){var expectedRow=new byte[image.Width];Marshal.Copy(expected.Ptr(row),expectedRow,0,expectedRow.Length);Assert.Equal(expectedRow,Pixels(actual,row));}
        using var color=new ImageFrameBuilder(8,8,PixelFormat.BGR8).Build();using var bad=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(new("edge","Canny","Canny",JsonDefaults.Element(p)),new Dictionary<string,object>{{"Image",color}});Assert.False(bad.Success);Assert.Contains("Mono8",bad.ErrorMessage??string.Join(";",bad.Nodes.Values.Select(n=>n.ErrorMessage)));
    }
}

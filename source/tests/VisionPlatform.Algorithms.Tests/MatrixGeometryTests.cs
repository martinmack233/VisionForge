using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Numerics;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class MatrixGeometryTests
{
    static NumericMatrix M(int rows,int columns,params double[] values)=>new(rows,columns,values);
    static ProjectiveTransform2D Affine(double x=0,double y=0,string source="image",string target="warped")=>MatrixGeometryProcessor.Create(M(2,3,1,0,x,0,1,y),GeometricTransformKind.Affine,source,target);
    static ImageFrame Gray(int width=8,int height=6,ImageCoordinateSpace? space=null)
    {using var b=new ImageFrameBuilder(width,height,PixelFormat.Mono8,width+7,space);for(var y=0;y<height;y++)for(var x=0;x<width;x++)b.GetRowSpan(y)[x]=(byte)(y*10+x);return b.Build();}
    static byte[] Row(ImageFrame image,int y){using var lease=image.Lease();return lease.GetRowSpan(y).ToArray();}
    static NodeSpecification N(string id,string type,object parameters)=>new(id,type,type,JsonDefaults.Element(parameters));
    [Fact] public void MatrixConstructionAndOperationsUseActualFloat64AndImmutableValues()
    {
        var data=new[]{4d,7,2,6};var a=M(2,2,data);data[0]=100;Assert.Equal(4,a[0,0]);var inverse=MatrixGeometryProcessor.Math(a,null,MatrixOperation.Inverse);Assert.Equal(.6,inverse[0,0],12);Assert.Equal(-.7,inverse[0,1],12);
        var product=MatrixGeometryProcessor.Math(a,inverse,MatrixOperation.Multiply);Assert.Equal(1,product[0,0],12);Assert.Equal(0,product[0,1],12);Assert.Equal(1,product[1,1],12);
        var transpose=MatrixGeometryProcessor.Math(M(2,3,1,2,3,4,5,6),null,MatrixOperation.Transpose);Assert.Equal(3,transpose.Rows);Assert.Equal(new[]{1d,4,2,5,3,6},transpose.Values);
        var parsed=MatrixGeometryProcessor.Parse(2,2,"1.23456789012345, 2\n3,4");Assert.Equal(1.23456789012345,parsed[0,0]);Assert.Throws<ArgumentOutOfRangeException>(()=>parsed[2,0]);
    }
    [Fact] public void MatrixErrorsAndCancellationAreExplicit()
    {
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Parse(2,2,"1,2"));Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Parse(1,1,"1+2"));Assert.Throws<ArgumentException>(()=>new NumericMatrix(1,1,[double.NaN]));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Math(M(2,2,1,2,2,4),null,MatrixOperation.Inverse));Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Math(M(2,2,1,1,1,1+1e-14),null,MatrixOperation.Inverse));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Math(M(1,2,1,2),M(1,1,1),MatrixOperation.Multiply));Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Math(M(1,2,1,2),null,MatrixOperation.Inverse));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>MatrixGeometryProcessor.Math(M(1,1,1),null,MatrixOperation.Transpose,cancel.Token));
    }
    [Fact] public void HomogeneousScaleInverseAndLegacyAffineBridgeHaveConsistentDirection()
    {
        var legacy=new CoordinateTransform2D(0,-2,2,0,10,20,"crop","image");var transform=ProjectiveTransform2D.FromAffine(legacy);var p=new Point2D(3,4);Assert.Equal(legacy.Map(p),transform.Map(p));Assert.Equal(legacy,transform.ToAffine());Assert.Equal(p,transform.Inverse().Map(transform.Map(p)));
        var scaled=new ProjectiveTransform2D(M(3,3,1e-100,0,1e-99,0,1e-100,2e-99,0,0,1e-100));Assert.Equal(new Point2D(13,24),scaled.Map(p));
        var perspective=new ProjectiveTransform2D(M(3,3,1,0,0,0,1,0,.02,0,1));Assert.False(perspective.IsAffine);Assert.Throws<InvalidOperationException>(()=>perspective.ToAffine());var mapped=perspective.Map(p);Assert.Equal(p.X,perspective.Inverse().Map(mapped).X,12);
        Assert.Throws<ArgumentException>(()=>new ProjectiveTransform2D(M(3,3,1,0,0,0,0,0,0,0,1)));
    }
    [Theory][InlineData(GeometricTransformKind.Affine)][InlineData(GeometricTransformKind.Perspective)]
    public void CorrespondingPointsRecoverKnownTransformWithoutAutomaticRegistrationClaim(GeometricTransformKind kind)
    {
        var known=kind==GeometricTransformKind.Affine?MatrixGeometryProcessor.Create(M(2,3,1.2,.2,10,-.1,.9,20),kind,"original","target"):new ProjectiveTransform2D(M(3,3,1.2,.2,10,-.1,.9,20,.001,-.002,1),"original","target");
        var source=new EdgePointSet([new(0,0),new(100,0),new(100,100),new(0,100),new(40,70)],"original");var target=new EdgePointSet(source.Points.Select(known.Map).ToArray(),"target");var solved=MatrixGeometryProcessor.Estimate(source,target,kind);Assert.Equal(5,solved.PairCount);Assert.InRange(solved.RootMeanSquareError,0,1e-5);var p=new Point2D(20,30);Assert.Equal(known.Map(p).X,solved.Transform.Map(p).X,5);Assert.Equal(known.Map(p).Y,solved.Transform.Map(p).Y,5);
    }
    [Theory][InlineData(GeometricTransformKind.Affine)][InlineData(GeometricTransformKind.Perspective)]
    public void CorrespondenceRejectsTooFewCollinearRepeatedAndMismatchedPoints(GeometricTransformKind kind)
    {
        var line=new EdgePointSet([new(0,0),new(1,1),new(2,2),new(3,3)]);Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Estimate(line,line,kind));
        var repeated=new EdgePointSet([new(0,0),new(0,0),new(1,0),new(0,1)]);Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Estimate(repeated,repeated,kind));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Estimate(new([new(0,0),new(1,1)]),line,kind));
    }
    [Fact] public void WarpAccountsForOriginalCoordinateOriginAndKeepsSourceUnchanged()
    {
        using var image=Gray(space:new("image",100,200));var before=Row(image,2);var transform=Affine(-100,-200);var result=MatrixGeometryProcessor.Warp(image,transform,new(){Width=8,Height=6,Interpolation=CropInterpolation.Nearest});using var output=result.Image;
        Assert.Equal(before,Row(output,2));Assert.Equal(before,Row(image,2));Assert.Equal(image.FrameId,output.FrameId);Assert.Equal("warped",output.CoordinateSpace.Id);Assert.Equal(new Point2D(100,200),result.InverseTransform.Map(new(0,0)));Assert.NotSame(image.BufferHandle,output.BufferHandle);
    }
    [Theory][InlineData(PixelFormat.Mono8)][InlineData(PixelFormat.RGB8)][InlineData(PixelFormat.BGR8)]
    public void AffineWarpPreservesSupportedFormatsAndSaturationFreeChannelValues(PixelFormat format)
    {
        using var builder=new ImageFrameBuilder(4,3,format);for(var y=0;y<3;y++)builder.GetRowSpan(y).Fill(127);using var image=builder.Build();var result=MatrixGeometryProcessor.Warp(image,Affine(1,0),new(){Width=5,Height=3,Interpolation=CropInterpolation.Nearest,BorderValue=9});using var output=result.Image;
        Assert.Equal(format,output.PixelFormat);var row=Row(output,0);Assert.Equal(9,row[0]);Assert.Equal(127,row[format.BytesPerPixel()]);
    }
    [Fact] public void PerspectiveWarpIsRealAndReturnsExplicitProjectiveCoordinates()
    {
        using var image=Gray(20,20);var transform=new ProjectiveTransform2D(M(3,3,1,0,0,0,1,0,.02,0,1));var result=MatrixGeometryProcessor.Warp(image,transform,new(){Width=20,Height=20,Interpolation=CropInterpolation.Nearest});using var output=result.Image;
        var point=transform.Map(new(10,10));Assert.Equal(10/1.2,point.X,12);Assert.Equal(10d,result.InverseTransform.Map(point).X,12);Assert.NotEqual(Row(image,10),Row(output,10));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Warp(image,transform,new(){Width=20,Height=20,Interpolation=CropInterpolation.Cubic}));
    }
    [Fact] public void WarpRejectsBayerWrongSpaceRotatedRoiInvalidDimensionsAndHorizon()
    {
        using var image=Gray();using var bayer=new ImageFrameBuilder(4,4,PixelFormat.BayerRG8).Build();Assert.Throws<NotSupportedException>(()=>MatrixGeometryProcessor.Warp(bayer,Affine(),new(){Width=4,Height=4}));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Warp(image,Affine(source:"other"),new(){Width=4,Height=4}));Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Warp(image,Affine(),new(){Width=0}));
        Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Warp(image,Affine(),new(){Width=4,Height=4},new(3,3,2,2,30)));
        var horizon=new ProjectiveTransform2D(M(3,3,1,0,0,0,1,0,1,0,-3));Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Warp(image,horizon,new(){Width=4,Height=4}));
    }
    [Fact] public void LargeInputRoiAllocatesOnlyRequestedOutputAndOwnedBuffersRelease()
    {
        var start=FrameHandle.ActiveBytes;using(var builder=new ImageFrameBuilder(8192,10000,PixelFormat.Mono8))
        {builder.GetRowSpan(9999).Slice(8000,4).Fill(191);using var image=builder.Build();var before=FrameHandle.ActiveBytes;var result=MatrixGeometryProcessor.Warp(image,Affine(-8000,-9997),new(){Width=4,Height=3,MaximumPixels=12,Interpolation=CropInterpolation.Nearest},new(8002,9998.5,4,3));using(var output=result.Image){Assert.Equal(12,output.BufferHandle.Length);Assert.Equal(before+12,FrameHandle.ActiveBytes);Assert.Equal(new byte[]{191,191,191,191},Row(output,2));}Assert.Equal(before,FrameHandle.ActiveBytes);}
        Assert.Equal(start,FrameHandle.ActiveBytes);
    }
    [Fact] public async Task RectangleMapsToQuadrilateralCollectionAndKeepsOriginalItemAssociation()
    {
        var roi=new RotatedRectRoi(20,20,10,10);var origin=new ItemOrigin(Guid.NewGuid(),"source",7,Guid.NewGuid(),"image","source-key");var collection=new VisionCollection(VisionItemKind.Roi,[new RoiItem(origin,roi)]);var transform=new ProjectiveTransform2D(M(3,3,1,0,0,0,1,0,.01,0,1));
        var node=new MapGeometryNode();var spec=N("map","MapGeometry",new MapGeometryOptions());var output=await node.ExecuteAsync(new(spec,new Dictionary<string,object>{{"Transform",transform},{"Collection",collection}},"."),default);var mapped=(VisionCollection)output.Values["Collection"];Assert.Equal(VisionItemKind.Contour,mapped.Kind);var item=Assert.IsType<ContourItem>(Assert.Single(mapped.Items));Assert.True(item.Closed);Assert.Equal(4,item.Points.Count);Assert.Equal("source-key",item.Origin.Key);Assert.Equal(origin.FrameId,item.Origin.FrameId);Assert.Equal("warped",item.Origin.CoordinateSpaceId);Assert.Null(item.Region);
        Assert.NotEqual(item.Points[0].Y,item.Points[1].Y);Assert.Equal(roi,((RoiItem)collection.Items[0]).Roi);
    }
    [Fact] public async Task RuntimeFlowAndParameterRoundTripExecuteIdenticallyAndCancelSafely()
    {
        var registry=BuiltinRegistration.CreateRegistry();var p=new MatrixCreateOptions{Rows=2,Columns=2,ValuesText="4,7;2,6"};var serialized=JsonDefaults.Element(p);Assert.Empty(registry.Create("MatrixCreate").ValidateParameters(serialized));
        var graph=new NodeGraph([N("create","MatrixCreate",p),N("inverse","MatrixMath",new MatrixMathOptions{Operation=MatrixOperation.Inverse}),N("element","MatrixElement",new MatrixElementOptions{Row=0,Column=1})],[new("create","Matrix","inverse","A"),new("inverse","Matrix","element","Matrix")]);
        var executor=new RuntimeExecutor(registry);using(var run=await executor.ExecuteAsync(graph)){Assert.True(run.Success,run.ErrorMessage);Assert.Equal(-.7,run.Nodes["element"].Output<double>("Value"),12);}
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();using var cancelled=await executor.ExecuteAsync(graph,cancellationToken:cancellation.Token);Assert.Equal(NodeExecutionStatus.Cancelled,cancelled.Status);
    }
    [Fact] public void MatrixMultiplyRequiresBWhileUnaryOperationsDoNot()
    {
        var registry=BuiltinRegistration.CreateRegistry();var validator=new GraphValidator(registry);
        NodeGraph Graph(MatrixOperation op)=>new([N("source","MatrixCreate",new MatrixCreateOptions()),N("math","MatrixMath",new MatrixMathOptions{Operation=op})],[new("source","Matrix","math","A")]);
        Assert.Contains(validator.Validate(Graph(MatrixOperation.Multiply)),e=>e.Contains("B"));Assert.Empty(validator.Validate(Graph(MatrixOperation.Transpose)));Assert.Empty(validator.Validate(Graph(MatrixOperation.Inverse)));
    }
    [Fact] public async Task MathematicalNodesParticipateInExistingSequentialSubflow()
    {
        var body=new SubflowDefinition{Nodes=[N("current","IterationInput",new{}),N("matrix","MatrixCreate",new MatrixCreateOptions{ValuesText="1.23456789012345,0;0,2"}),N("value","MatrixElement",new MatrixElementOptions()),N("collect","IterationOutput",new{})],Connections=[new("matrix","Matrix","value","Matrix"),new("value","Value","collect","Number")]};
        var items=new VisionCollection(VisionItemKind.Roi,Enumerable.Range(0,3).Select(i=>new RoiItem(new(Guid.NewGuid(),"source",i,null,"image","item-"+i),new(10,10,2,2))));
        using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(N("each","ForEach",new ForEachOptions{Body=body}),new Dictionary<string,object>{{"Collection",items}});
        Assert.True(run.Success,run.ErrorMessage);var batch=run.Nodes["each"].Output<IterationBatch>("Batch");Assert.Equal(3,batch.CompletedCount);foreach(var item in batch.Results.Items.Cast<IterationItem>())Assert.Equal(1.23456789012345,item.Result!.Number);
    }
    [Fact] public void TinyNonzeroPerspectiveTermNeverBecomesAnAffineApproximation()
    {
        var map=new ProjectiveTransform2D(M(3,3,1,0,0,0,1,0,1e-16,0,1));Assert.False(map.IsAffine);Assert.Throws<InvalidOperationException>(()=>map.ToAffine());
        var four=new EdgePointSet([new(0,0),new(1,0),new(2,0),new(0,1)]);Assert.Throws<ArgumentException>(()=>MatrixGeometryProcessor.Estimate(four,four,GeometricTransformKind.Perspective));
    }
}

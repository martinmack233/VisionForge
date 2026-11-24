using System.Text.Json;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Numerics;

namespace VisionPlatform.Plugins.Builtin;

[SubflowSafe]
public sealed class MatrixCreateNode : NodeBase<MatrixCreateOptions>
{
    public override NodeDescriptor Descriptor => Describe("MatrixCreate", [], [new("Matrix", typeof(NumericMatrix))]);
    protected override IReadOnlyList<string> Validate(MatrixCreateOptions p)
    { try { if (p.Rows is < 1 or > 1000 || p.Columns is < 1 or > 1000) return ["矩阵行列各自必须在1..1000。"]; MatrixGeometryProcessor.Parse(p.Rows, p.Columns, p.ValuesText); return []; } catch (Exception ex) { return [ex.Message]; } }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); var p = c.Parameters<MatrixCreateOptions>(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Matrix"] = MatrixGeometryProcessor.Parse(p.Rows, p.Columns, p.ValuesText) })); }
}
[SubflowSafe]
public sealed class MatrixMathNode : NodeBase<MatrixMathOptions>, IParameterDependentInputs
{
    public override NodeDescriptor Descriptor => Describe("MatrixMath", [new("A", typeof(NumericMatrix)), new("B", typeof(NumericMatrix), false)], [new("Matrix", typeof(NumericMatrix))]);
    protected override IReadOnlyList<string> Validate(MatrixMathOptions p) => Enum.IsDefined(p.Operation) ? [] : ["请选择有效矩阵运算。"]; 
    public IReadOnlyList<string> RequiredInputs(System.Text.Json.JsonElement parameters) => parameters.Deserialize<MatrixMathOptions>(JsonDefaults.Options)?.Operation == MatrixOperation.Multiply ? ["B"] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    { var p = c.Parameters<MatrixMathOptions>(); var result = MatrixGeometryProcessor.Math(c.Input<NumericMatrix>("A"), c.TryInput<NumericMatrix>("B", out var b) ? b : null, p.Operation, ct); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Matrix"] = result })); }
}
[SubflowSafe]
public sealed class MatrixElementNode : NodeBase<MatrixElementOptions>
{
    public override NodeDescriptor Descriptor => Describe("MatrixElement", [new("Matrix", typeof(NumericMatrix))], [new("Value", typeof(double))]);
    protected override IReadOnlyList<string> Validate(MatrixElementOptions p) => p.Row is < 0 or > 999 || p.Column is < 0 or > 999 ? ["矩阵行列索引范围0..999，且运行时不得超过实际尺寸。"] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); var p = c.Parameters<MatrixElementOptions>(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Value"] = c.Input<NumericMatrix>("Matrix")[p.Row, p.Column] })); }
}
[SubflowSafe]
public sealed class TransformCreateNode : NodeBase<TransformCreateOptions>
{
    public override NodeDescriptor Descriptor => Describe("TransformCreate", [new("Matrix", typeof(NumericMatrix), false), new("Affine", typeof(CoordinateTransform2D), false)],
        [new("Transform", typeof(ProjectiveTransform2D)), new("InverseTransform", typeof(ProjectiveTransform2D)), new("Matrix", typeof(NumericMatrix))]);
    protected override IReadOnlyList<string> Validate(TransformCreateOptions p)
    {
        if (!Enum.IsDefined(p.Kind) || string.IsNullOrWhiteSpace(p.SourceSpaceId) || string.IsNullOrWhiteSpace(p.TargetSpaceId) || p.SourceSpaceId.Length > 256 || p.TargetSpaceId.Length > 256) return ["变换类型及源/目标坐标空间无效。"];
        // Connected matrices may replace literal coefficients; validate its syntax only at execution.
        if (p.MatrixText is null || p.MatrixText.Length > 10000) return ["矩阵系数文本无效或过长。"];
        return [];
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var p = c.Parameters<TransformCreateOptions>();
        if (c.Inputs.ContainsKey("Matrix") && c.Inputs.ContainsKey("Affine")) throw new ArgumentException("Matrix与Affine只连接一种，不能隐式混合变换。");
        var transform = c.TryInput<CoordinateTransform2D>("Affine", out var affine) ? ProjectiveTransform2D.FromAffine(affine) : MatrixGeometryProcessor.Create(c.TryInput<NumericMatrix>("Matrix", out var matrix) ? matrix : MatrixGeometryProcessor.Parse(p.Kind == GeometricTransformKind.Affine ? 2 : 3, 3, p.MatrixText), p.Kind, p.SourceSpaceId, p.TargetSpaceId);
        if (p.Invert) transform = transform.Inverse();
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Transform"] = transform, ["InverseTransform"] = transform.Inverse(), ["Matrix"] = transform.Matrix }));
    }
}
[SubflowSafe]
public sealed class EstimateTransformNode : NodeBase<EstimateTransformOptions>
{
    public override NodeDescriptor Descriptor => Describe("EstimateTransform", [new("SourcePoints", typeof(EdgePointSet), false), new("TargetPoints", typeof(EdgePointSet), false)],
        [new("Transform", typeof(ProjectiveTransform2D)), new("Matrix", typeof(NumericMatrix)), new("Residual", typeof(double)), new("PairCount", typeof(int))]);
    protected override IReadOnlyList<string> Validate(EstimateTransformOptions p) => !Enum.IsDefined(p.Kind) || string.IsNullOrWhiteSpace(p.SourceSpaceId) || string.IsNullOrWhiteSpace(p.TargetSpaceId) || p.SourcePointsText is null || p.TargetPointsText is null || p.SourcePointsText.Length > 100000 || p.TargetPointsText.Length > 100000 ? ["变换类型、坐标空间或对应点文本无效。"] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var p = c.Parameters<EstimateTransformOptions>();
        if (c.Inputs.ContainsKey("SourcePoints") != c.Inputs.ContainsKey("TargetPoints")) throw new ArgumentException("对应点端口须成对连接；否则使用两组参数点。");
        var source = c.TryInput<EdgePointSet>("SourcePoints", out var s) ? s : MatrixGeometryProcessor.ParsePoints(p.SourcePointsText, p.SourceSpaceId);
        var target = c.TryInput<EdgePointSet>("TargetPoints", out var t) ? t : MatrixGeometryProcessor.ParsePoints(p.TargetPointsText, p.TargetSpaceId);
        var result = MatrixGeometryProcessor.Estimate(source, target, p.Kind, ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Transform"] = result.Transform, ["Matrix"] = result.Transform.Matrix, ["Residual"] = result.RootMeanSquareError, ["PairCount"] = result.PairCount }));
    }
}
[SubflowSafe("ROI")]
public sealed class WarpImageNode : NodeBase<WarpImageOptions>
{
    public override NodeDescriptor Descriptor => Describe("WarpImage", [new("Image", typeof(ImageFrame)), new("Transform", typeof(ProjectiveTransform2D)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("TransformedImage", typeof(ImageFrame)), new("Transform", typeof(ProjectiveTransform2D)), new("InverseTransform", typeof(ProjectiveTransform2D))]);
    protected override IReadOnlyList<string> Validate(WarpImageOptions p) => p.Width is < 1 or > 32766 || p.Height is < 1 or > 32766 || p.MaximumPixels is < 1 or > 100000000 || (long)p.Width * p.Height > p.MaximumPixels || !Enum.IsDefined(p.Interpolation) || !Enum.IsDefined(p.BorderMode) ? ["输出宽高、插值、边界或像素预算无效。"] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var result = MatrixGeometryProcessor.Warp(c.Input<ImageFrame>("Image"), c.Input<ProjectiveTransform2D>("Transform"), c.Parameters<WarpImageOptions>(), c.TryInput<RotatedRectRoi>("ROI", out var roi) ? roi : null, ct);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["TransformedImage"] = result.Image, ["Transform"] = result.Transform, ["InverseTransform"] = result.InverseTransform }, [result.Image]));
    }
}
[SubflowSafe]
public sealed class MapGeometryNode : NodeBase<MapGeometryOptions>
{
    public override NodeDescriptor Descriptor => Describe("MapGeometry", [new("Transform", typeof(ProjectiveTransform2D)), new("Point", typeof(Point2D), false), new("Points", typeof(EdgePointSet), false), new("ROI", typeof(RotatedRectRoi), false), new("Collection", typeof(VisionCollection), false)],
        [new("MappedPoint", typeof(Point2D), false), new("MappedPoints", typeof(EdgePointSet), false), new("Collection", typeof(VisionCollection))]);
    protected override IReadOnlyList<string> Validate(MapGeometryOptions p) => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || string.IsNullOrWhiteSpace(p.CoordinateSpaceId) ? ["参数点必须为有限数值，且坐标空间明确。"] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var transform = c.Input<ProjectiveTransform2D>("Transform"); var p = c.Parameters<MapGeometryOptions>();
        if (new[] { "Point", "Points", "ROI", "Collection" }.Count(c.Inputs.ContainsKey) > 1) throw new ArgumentException("点、点集、ROI或集合只连接一种；不隐式配对或拼接。");
        var outputs = new Dictionary<string, object>();
        ItemOrigin Origin(int i) => new(c.RunId, c.Spec.NodeId, i, null, transform.TargetSpaceId, $"{c.RunId:N}/{c.Spec.NodeId}/{i}");
        if (c.TryInput<VisionCollection>("Collection", out var collection))
        {
            if (collection.Count > 10000) throw new ArgumentException("几何集合超过10000项预算。");
            if (collection.Kind is not (VisionItemKind.Roi or VisionItemKind.Blob or VisionItemKind.Detection or VisionItemKind.Contour or VisionItemKind.PointSet)) throw new ArgumentException("当前集合仅支持ROI/Blob/检测框→四边形、轮廓和点集；不把透视圆冒充圆。");
            if (collection.Items.Sum(i => i is PointSetItem ps ? (long)ps.Points.Count : i is ContourItem contour ? contour.Points.Count : 4) > 1_000_000) throw new ArgumentException("整个映射集合最多1000000个点，请显式分批处理。");
            var mapped = new List<VisionItem>(); var targetKind = collection.Kind == VisionItemKind.PointSet ? VisionItemKind.PointSet : VisionItemKind.Contour;
            foreach (var item in collection.Items)
            {
                ct.ThrowIfCancellationRequested(); if (item.Origin.CoordinateSpaceId != transform.SourceSpaceId) throw new ArgumentException("集合项源坐标空间不一致，需显式转换。");
                var origin = item.Origin with { CoordinateSpaceId = transform.TargetSpaceId }; // Key/RunId/SourceIndex/FrameId retain the source association.
                if (item is PointSetItem pointSet) mapped.Add(new PointSetItem(origin, MatrixGeometryProcessor.Map(new(pointSet.Points, transform.SourceSpaceId), transform, ct).Points));
                else if (item is ContourItem contour) { MatrixGeometryProcessor.ValidateConnectedDomain(transform, contour.Points); var pts = MatrixGeometryProcessor.Map(new(contour.Points, transform.SourceSpaceId), transform, ct); mapped.Add(new ContourItem(origin, pts.Points, contour.Closed) { ParentIndex = contour.ParentIndex, IsHole = contour.IsHole }); }
                else if (item.Region is { } region) mapped.Add(new ContourItem(origin, MatrixGeometryProcessor.MapRectangle(region, transform, ct).Points, true));
                else throw new ArgumentException("此项缺少可映射的明确几何，不返回假四边形。");
            }
            outputs["Collection"] = new VisionCollection(targetKind, mapped);
        }
        else if (c.TryInput<RotatedRectRoi>("ROI", out var roi))
        { var pts = MatrixGeometryProcessor.MapRectangle(roi, transform, ct); outputs["MappedPoints"] = pts; outputs["Collection"] = new VisionCollection(VisionItemKind.Contour, [new ContourItem(Origin(0), pts.Points, true)]); }
        else
        {
            EdgePointSet input; var single = !c.Inputs.ContainsKey("Points");
            if (c.TryInput<EdgePointSet>("Points", out var points)) input = points;
            else { if (p.CoordinateSpaceId != transform.SourceSpaceId) throw new ArgumentException("点的源坐标空间与变换不一致。"); input = new([c.TryInput<Point2D>("Point", out var point) ? point : new(p.X, p.Y)], p.CoordinateSpaceId); }
            var mapped = MatrixGeometryProcessor.Map(input, transform, ct); outputs["MappedPoints"] = mapped; if (single) outputs["MappedPoint"] = mapped.Points[0]; outputs["Collection"] = new VisionCollection(VisionItemKind.PointSet, [new PointSetItem(Origin(0), mapped.Points)]);
        }
        return ValueTask.FromResult(new NodeOutput(outputs));
    }
}

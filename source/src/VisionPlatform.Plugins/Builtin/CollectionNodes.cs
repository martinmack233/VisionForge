using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record EmptyCollectionOptions;
public sealed record CollectionAdapterOptions
{
    [DisplayName("最多适配项目数"), Range(1, 100000)] public int MaximumItems { get; init; } = 10000;
}
/// <summary>Explicit typed boundary to existing results; never selects an item implicitly.</summary>
[SubflowSafe]
public sealed class CollectionAdapterNode : NodeBase<CollectionAdapterOptions>
{
    protected override IReadOnlyList<string> Validate(CollectionAdapterOptions p) => p.MaximumItems is < 1 or > 100000 ? ["MaximumItems must be in 1..100000."] : [];
    public override NodeDescriptor Descriptor => Describe("CollectionAdapter",
        [new("Blobs", typeof(BlobResults), false), new("Detections", typeof(YoloInferenceResult), false), new("OCR", typeof(OcrResult), false),
         new("Codes", typeof(CodeReadResult), false), new("ROI", typeof(RotatedRectRoi), false), new("Points", typeof(EdgePointSet), false),
         new("Line", typeof(LineGeometry), false), new("Circle", typeof(CircleGeometry), false), new("Measurement", typeof(MeasurementResult), false),
         new("Contour", typeof(RecognitionOverlay), false), new("Image", typeof(ImageFrame), false)], [new("Collection", typeof(VisionCollection))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (c.Inputs.Count(p => p.Key != "Image") != 1) throw new ArgumentException("Connect exactly one typed result to CollectionAdapter; Image supplies optional frame identity only.");
        var image = c.TryInput<ImageFrame>("Image", out var f) ? f : null;
        ItemOrigin Origin(int i, string space, Guid? frame = null)
        {
            if (image is not null && (image.CoordinateSpace.Id != space || frame is { } id && id != Guid.Empty && image.FrameId != id))
                throw new ArgumentException("Result/image coordinate space or frame identity mismatch; supply an explicit coordinate transform first.");
            return new(c.RunId, c.Spec.NodeId, i, frame is { } g && g != Guid.Empty ? g : image?.FrameId, space, $"{c.RunId:N}/{c.Spec.NodeId}/{i}");
        }
        VisionCollection result;
        if (c.TryInput<BlobResults>("Blobs", out var b)) result = new(VisionItemKind.Blob, b.Items.Select((v, i) => new BlobItem(Origin(i, b.CoordinateSpaceId), v with { PixelRuns = v.PixelRuns is null ? null : Array.AsReadOnly(v.PixelRuns.ToArray()) })));
        else if (c.TryInput<YoloInferenceResult>("Detections", out var y))
        {
            if (!y.Success) throw new ArgumentException(y.FailureReason ?? "Detection did not succeed.");
            result = new(VisionItemKind.Detection, y.Detections.Select((v, i) => new DetectionItem(Origin(i, y.CoordinateSpaceId, y.FrameId), v)));
        }
        else if (c.TryInput<OcrResult>("OCR", out var o))
        {
            if (o.Status is OcrStatus.MissingLanguageModel or OcrStatus.EngineError or OcrStatus.ResourceLimit)
                throw new ArgumentException(o.FailureReason ?? "OCR engine/resource error; this is not an empty successful collection.");
            result = new(VisionItemKind.Text, o.Words.Select((v, i) => new TextItem(Origin(i, o.CoordinateSpaceId), v)
                { RecognitionState = o.Success ? BusinessState.Ok : BusinessState.Unrecognized }));
        }
        else if (c.TryInput<CodeReadResult>("Codes", out var code)) result = new(VisionItemKind.Code, code.Codes.Select((v, i) => new CodeItem(Origin(i, code.CoordinateSpaceId), v.Text, v.Format, Array.AsReadOnly(v.Points.ToArray()))));
        else if (c.TryInput<RotatedRectRoi>("ROI", out var r)) result = new(VisionItemKind.Roi, [new RoiItem(Origin(0, r.CoordinateSpaceId), r)]);
        else if (c.TryInput<EdgePointSet>("Points", out var p)) result = new(VisionItemKind.PointSet, [new PointSetItem(Origin(0, p.CoordinateSpaceId), Array.AsReadOnly(p.Points.ToArray()))]);
        else if (c.TryInput<LineGeometry>("Line", out var l)) result = new(VisionItemKind.Line, [new LineItem(Origin(0, l.CoordinateSpaceId), l)]);
        else if (c.TryInput<CircleGeometry>("Circle", out var circle)) result = new(VisionItemKind.Circle, [new CircleItem(Origin(0, circle.CoordinateSpaceId), circle)]);
        else if (c.TryInput<MeasurementResult>("Measurement", out var m)) result = new(VisionItemKind.Measurement, [new MeasurementItem(Origin(0, m.CoordinateSpaceId), m)]);
        else if (c.TryInput<RecognitionOverlay>("Contour", out var contour))
        {
            if (contour.Kind is not (RecognitionOverlayKind.Polygon or RecognitionOverlayKind.Polyline)) throw new ArgumentException("Only ordered Polygon/Polyline overlays define a contour; point sets do not.");
            result = new(VisionItemKind.Contour, [new ContourItem(Origin(0, contour.CoordinateSpaceId), Array.AsReadOnly(contour.Points.ToArray()), contour.Kind == RecognitionOverlayKind.Polygon)]);
        }
        else throw new ArgumentException("Unsupported typed collection input.");
        if (result.Count > c.Parameters<CollectionAdapterOptions>().MaximumItems) throw new ArgumentException("Collection adapter MaximumItems exceeded; reduce the upstream result count or explicitly increase the budget.");
        return Output(result);
    }
    internal static ValueTask<NodeOutput> Output(VisionCollection collection) => ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Collection"] = collection }));
}
public sealed record RoiCollectionOptions
{
    [DisplayName("最多ROI数量"), Range(1, 10000)] public int MaximumItems { get; init; } = 1000;
    [Browsable(false)] public List<RotatedRectRoi> Regions { get; init; } = [];
}
[SubflowSafe]
public sealed class RoiCollectionNode : NodeBase<RoiCollectionOptions>
{
    public override NodeDescriptor Descriptor => Describe("RoiCollection", [new("Image", typeof(ImageFrame), false)], [new("Collection", typeof(VisionCollection))]);
    protected override IReadOnlyList<string> Validate(RoiCollectionOptions p)
    {
        try { if (p.MaximumItems is < 1 or > 10000 || p.Regions is null || p.Regions.Count > p.MaximumItems) return ["ROI collection exceeds the configured 1..10000 item budget."]; foreach (var roi in p.Regions) roi.Validate(); return []; }
        catch (Exception ex) { return [ex.Message]; }
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var frame = c.TryInput<ImageFrame>("Image", out var image) ? image : null;
        var items = c.Parameters<RoiCollectionOptions>().Regions.Select((r, i) =>
        {
            if (frame is not null && frame.CoordinateSpace.Id != r.CoordinateSpaceId) throw new ArgumentException("ROI/image coordinate space mismatch.");
            return new RoiItem(new(c.RunId, c.Spec.NodeId, i, frame?.FrameId, r.CoordinateSpaceId, $"{c.RunId:N}/{c.Spec.NodeId}/{i}"), r);
        });
        return CollectionAdapterNode.Output(new(VisionItemKind.Roi, items));
    }
}
public sealed record CollectionCountOptions
{
    [DisplayName("仅统计指定业务状态")] public bool FilterState { get; init; }
    [DisplayName("业务状态")] public BusinessState State { get; init; }
}
[SubflowSafe]
public sealed class CollectionCountNode : NodeBase<CollectionCountOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionCount", [new("Collection", typeof(VisionCollection))], [new("Count", typeof(int))]);
    protected override IReadOnlyList<string> Validate(CollectionCountOptions p) => Enum.IsDefined(p.State) ? [] : ["Invalid business state."];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); var p = c.Parameters<CollectionCountOptions>(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Count"] = c.Input<VisionCollection>("Collection").Items.Count(i => !p.FilterState || i.State == p.State) })); }
}
public sealed record CollectionIndexOptions { [DisplayName("索引（从0开始）"), Range(0, int.MaxValue)] public int Index { get; init; } }
[SubflowSafe]
public sealed class CollectionSelectNode : NodeBase<CollectionIndexOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionSelect", [new("Collection", typeof(VisionCollection))], [new("Item", typeof(VisionItem))]);
    protected override IReadOnlyList<string> Validate(CollectionIndexOptions p) => p.Index < 0 ? ["Index is zero-based and must be nonnegative."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var items = c.Input<VisionCollection>("Collection"); var i = c.Parameters<CollectionIndexOptions>().Index;
        if (i < 0 || i >= items.Count) throw new ArgumentOutOfRangeException(nameof(i), $"Zero-based index {i} is outside collection count {items.Count}.");
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Item"] = items.Items[i] }));
    }
}
public enum CollectionSliceMode { All, FirstN, Range }
public sealed record CollectionSliceOptions
{
    public CollectionSliceMode Mode { get; init; }
    [Range(0, int.MaxValue)] public int Start { get; init; }
    [Range(0, int.MaxValue)] public int Count { get; init; } = 10;
}
[SubflowSafe]
public sealed class CollectionSliceNode : NodeBase<CollectionSliceOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionSlice", [new("Collection", typeof(VisionCollection))], [new("Collection", typeof(VisionCollection))]);
    protected override IReadOnlyList<string> Validate(CollectionSliceOptions p) => !Enum.IsDefined(p.Mode) || p.Start < 0 || p.Count < 0 ? ["Invalid slice mode/start/count."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var source = c.Input<VisionCollection>("Collection"); var p = c.Parameters<CollectionSliceOptions>();
        if (p.Mode == CollectionSliceMode.Range && (p.Start > source.Count || (long)p.Start + p.Count > source.Count)) throw new ArgumentException("Requested range exceeds collection; range is [Start, Start+Count).");
        return CollectionAdapterNode.Output(source.With(p.Mode == CollectionSliceMode.All ? source.Items : source.Items.Skip(p.Mode == CollectionSliceMode.FirstN ? 0 : p.Start).Take(p.Count)));
    }
}
public enum CollectionComparison { Equal, NotEqual, Greater, GreaterOrEqual, Less, LessOrEqual, Contains }
public sealed record CollectionFilterOptions
{
    [DisplayName("字段（区分实际支持能力）")] public string Field { get; init; } = "Area";
    public CollectionComparison Comparison { get; init; } = CollectionComparison.GreaterOrEqual;
    public double Number { get; init; }
    public string Text { get; init; } = "";
    public bool Boolean { get; init; }
}
public static class CollectionFields
{
    public static readonly string[] Geometry = ["X", "Y", "Width", "Height", "Angle"];
    public static IEnumerable<string> Supported(VisionCollection c) => c.Kind switch
    {
        VisionItemKind.Roi => Geometry,
        VisionItemKind.Blob => Geometry.Concat(["Area", "Label", "Perimeter", "Circularity"]),
        VisionItemKind.Detection => Geometry.Concat(["ClassId", "Class", "Confidence"]),
        VisionItemKind.Text => Geometry.Concat(["Text", "Confidence"]),
        VisionItemKind.Contour => Geometry.Concat(["PointCount", "Closed", "ParentIndex", "IsHole", "Area", "SignedArea", "Perimeter", "Circularity", "AspectRatio", "Rectangularity", "EllipseWidth", "EllipseHeight", "EllipseAngle", "AnalysisDetail"]), VisionItemKind.PointSet => ["PointCount"],
        VisionItemKind.Line => ["X", "Y", "EndX", "EndY", "Length", "DirectionX", "DirectionY"], VisionItemKind.Circle => ["X", "Y", "Radius"],
        VisionItemKind.Code => ["Text", "Format"],
        _ => c.Items.SelectMany(i => i.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
    };
    public static void Check(VisionCollection c, string field)
    {
        if (!Supported(c).Contains(field, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Field '{field}' is unsupported for {c.Kind}. Available: {string.Join(", ", Supported(c))}");
        if (c.Items.Any(i => !i.Fields.ContainsKey(field))) throw new ArgumentException($"Field '{field}' was not actually measured on every item; missing metrics are not zero.");
    }
}
[SubflowSafe]
public sealed class CollectionFilterNode : NodeBase<CollectionFilterOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionFilter", [new("Collection", typeof(VisionCollection))], [new("Collection", typeof(VisionCollection))]);
    protected override IReadOnlyList<string> Validate(CollectionFilterOptions p) => string.IsNullOrWhiteSpace(p.Field) || !Enum.IsDefined(p.Comparison) || !double.IsFinite(p.Number) || p.Text is null ? ["Invalid filter field/comparison/value."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var source = c.Input<VisionCollection>("Collection"); var p = c.Parameters<CollectionFilterOptions>(); CollectionFields.Check(source, p.Field);
        return CollectionAdapterNode.Output(source.With(source.Items.Where(i =>
        {
            ct.ThrowIfCancellationRequested(); var v = i.Fields[p.Field];
            if (v.Number is { } n && p.Comparison == CollectionComparison.Contains) throw new ArgumentException("Contains is only valid for text.");
            if (p.Comparison == CollectionComparison.Contains) return v.Text?.Contains(p.Text, StringComparison.Ordinal) ?? throw new ArgumentException("Contains requires text.");
            var order = v.Number is { } number ? number.CompareTo(p.Number) : v.Text is { } text ? string.CompareOrdinal(text, p.Text) : v.Boolean?.CompareTo(p.Boolean) ?? 0;
            return p.Comparison switch { CollectionComparison.Equal => order == 0, CollectionComparison.NotEqual => order != 0, CollectionComparison.Greater => order > 0,
                CollectionComparison.GreaterOrEqual => order >= 0, CollectionComparison.Less => order < 0, CollectionComparison.LessOrEqual => order <= 0, _ => false };
        })));
    }
}
public sealed record CollectionSortOptions { public string Field { get; init; } = "X"; public bool Descending { get; init; } }
[SubflowSafe]
public sealed class CollectionSortNode : NodeBase<CollectionSortOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionSort", [new("Collection", typeof(VisionCollection))], [new("Collection", typeof(VisionCollection))]);
    protected override IReadOnlyList<string> Validate(CollectionSortOptions p) => string.IsNullOrWhiteSpace(p.Field) ? ["Sort field is required."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var source = c.Input<VisionCollection>("Collection"); var p = c.Parameters<CollectionSortOptions>(); CollectionFields.Check(source, p.Field);
        var comparer = Comparer<CollectionValue>.Create((a, b) => a.Number.HasValue && b.Number.HasValue ? a.Number.Value.CompareTo(b.Number.Value) : a.Boolean.HasValue && b.Boolean.HasValue ? a.Boolean.Value.CompareTo(b.Boolean.Value) : string.CompareOrdinal(a.Text, b.Text));
        return CollectionAdapterNode.Output(source.With(p.Descending ? source.Items.OrderByDescending(i => i.Fields[p.Field], comparer) : source.Items.OrderBy(i => i.Fields[p.Field], comparer)));
    }
}
public sealed record ItemRoiOptions { [DisplayName("显式使用轴对齐外包框")] public bool AxisAlignedEnvelope { get; init; } }
public static class ItemRoiConversion
{
    public static RotatedRectRoi Convert(VisionItem item, CoordinateTransform2D? transform, bool envelope)
    {
        var region = item.Region;
        if (region is null && envelope)
        {
            IReadOnlyList<Point2D>? points = item switch { ContourItem c => c.Points, CodeItem c => c.Points, PointSetItem p => p.Points, _ => null };
            if (item is CircleItem circle) region = new(circle.Circle.Center.X, circle.Circle.Center.Y, circle.Circle.Radius * 2, circle.Circle.Radius * 2, 0, item.Origin.CoordinateSpaceId);
            else if (points is { Count: > 1 }) region = Bounds(points, item.Origin.CoordinateSpaceId);
        }
        if (region is null) throw new ArgumentException($"{item.Kind} has no rectangular ROI. For contours/circles/codes explicitly enable AxisAlignedEnvelope; lines have no finite-width ROI.");
        region.Validate();
        if (region.CoordinateSpaceId != item.Origin.CoordinateSpaceId) throw new ArgumentException("Item/ROI coordinate space mismatch.");
        if (transform is null) return region;
        if (transform.SourceSpaceId != region.CoordinateSpaceId) throw new ArgumentException("Transform source does not match item coordinate space.");
        var corners = region.Corners().Select(transform.Map).ToArray();
        if (envelope) return Bounds(corners, transform.TargetSpaceId);
        var ux = corners[1].X - corners[0].X; var uy = corners[1].Y - corners[0].Y;
        var vx = corners[3].X - corners[0].X; var vy = corners[3].Y - corners[0].Y;
        var w = Math.Sqrt(ux * ux + uy * uy); var h = Math.Sqrt(vx * vx + vy * vy);
        if (w <= 0 || h <= 0 || Math.Abs(ux * vx + uy * vy) > 1e-8 * w * h) throw new ArgumentException("Transform introduces shear/degenerate geometry; explicitly choose an axis-aligned envelope or a different ROI.");
        var center = transform.Map(new(region.CenterX, region.CenterY));
        var result = new RotatedRectRoi(center.X, center.Y, w, h, Math.Atan2(uy, ux) * 180 / Math.PI, transform.TargetSpaceId); result.Validate(); return result;
    }
    public static ItemOrigin Origin(VisionItem item, RotatedRectRoi region, ImageFrame? target)
    {
        if (target is not null && target.CoordinateSpace.Id != region.CoordinateSpaceId) throw new ArgumentException("TargetImage does not match transformed ROI space.");
        if (item.Origin.FrameId is not null && item.Origin.CoordinateSpaceId != region.CoordinateSpaceId && target is null)
            throw new ArgumentException("Mapping a known local frame into another space requires explicit TargetImage identity; no frame identity is guessed.");
        return item.Origin with { CoordinateSpaceId = region.CoordinateSpaceId, FrameId = target?.FrameId ?? item.Origin.FrameId };
    }
    private static RotatedRectRoi Bounds(IReadOnlyList<Point2D> p, string space)
    { var result = new RectRoi(p.Min(v => v.X), p.Min(v => v.Y), p.Max(v => v.X) - p.Min(v => v.X), p.Max(v => v.Y) - p.Min(v => v.Y)).ToRotated(space); result.Validate(); return result; }
}
[SubflowSafe]
public sealed class ItemToRoiNode : NodeBase<ItemRoiOptions>
{
    public override NodeDescriptor Descriptor => Describe("ItemToRoi", [new("Item", typeof(VisionItem)), new("Transform", typeof(CoordinateTransform2D), false), new("TargetImage", typeof(ImageFrame), false)], [new("ROI", typeof(RotatedRectRoi)), new("Item", typeof(VisionItem)), new("Center", typeof(Point2D)), new("Right", typeof(Point2D))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var item = c.Input<VisionItem>("Item"); var roi = ItemRoiConversion.Convert(item, c.TryInput<CoordinateTransform2D>("Transform", out var t) ? t : null, c.Parameters<ItemRoiOptions>().AxisAlignedEnvelope);
        var angle = roi.AngleDegrees * Math.PI / 180;
        var origin = ItemRoiConversion.Origin(item, roi, c.TryInput<ImageFrame>("TargetImage", out var target) ? target : null);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["ROI"] = roi, ["Item"] = new RoiItem(origin, roi) { OriginalItem = item is RoiItem existing ? existing.OriginalItem : item },
            ["Center"] = new Point2D(roi.CenterX, roi.CenterY), ["Right"] = new Point2D(roi.CenterX + Math.Cos(angle) * roi.Width / 2, roi.CenterY + Math.Sin(angle) * roi.Width / 2) }));
    }
}
[SubflowSafe]
public sealed class CollectionToRoisNode : NodeBase<ItemRoiOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionToRois", [new("Collection", typeof(VisionCollection)), new("Transform", typeof(CoordinateTransform2D), false), new("TargetImage", typeof(ImageFrame), false)], [new("Collection", typeof(VisionCollection))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var source = c.Input<VisionCollection>("Collection"); var t = c.TryInput<CoordinateTransform2D>("Transform", out var v) ? v : null; var p = c.Parameters<ItemRoiOptions>();
        var target = c.TryInput<ImageFrame>("TargetImage", out var image) ? image : null;
        return CollectionAdapterNode.Output(new(VisionItemKind.Roi, source.Items.Select(item => { ct.ThrowIfCancellationRequested(); var r = ItemRoiConversion.Convert(item, t, p.AxisAlignedEnvelope); return new RoiItem(ItemRoiConversion.Origin(item, r, target), r) { OriginalItem = item is RoiItem existing ? existing.OriginalItem : item }; })));
    }
}

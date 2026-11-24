using System.Collections.ObjectModel;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Contracts.Collections;

public enum VisionItemKind { Roi, Blob, Detection, Contour, PointSet, Line, Circle, Text, Code, Measurement, Iteration, Scalar }
public enum BusinessState { Ok, Ng, Unrecognized, Error, Unfinished }
/// <summary>An execution association, never a cross-frame tracking identifier.</summary>
public sealed record ItemOrigin(Guid RunId, string NodeId, int SourceIndex, Guid? FrameId, string CoordinateSpaceId, string Key);
public abstract record VisionItem(ItemOrigin Origin)
{
    public abstract VisionItemKind Kind { get; }
    public abstract IReadOnlyDictionary<string, CollectionValue> Fields { get; }
    public virtual RotatedRectRoi? Region => null;
    public virtual BusinessState State => BusinessState.Ok;
    protected static IReadOnlyDictionary<string, CollectionValue> Data(params (string, CollectionValue)[] values) =>
        new ReadOnlyDictionary<string, CollectionValue>(values.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase));
    protected static (string, CollectionValue)[] Geometry(RotatedRectRoi r) =>
        [("X", r.CenterX), ("Y", r.CenterY), ("Width", r.Width), ("Height", r.Height), ("Angle", r.AngleDegrees)];
}
public readonly record struct CollectionValue(double? Number, string? Text, bool? Boolean)
{
    public static implicit operator CollectionValue(double value) => new(value, null, null);
    public static implicit operator CollectionValue(string value) => new(null, value, null);
    public static implicit operator CollectionValue(bool value) => new(null, null, value);
    public string Format(int decimals = 3) => Number?.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture) ?? Text ?? Boolean?.ToString() ?? "";
}
public sealed record RoiItem(ItemOrigin Source, RotatedRectRoi Roi) : VisionItem(Source)
{
    public VisionItem? OriginalItem { get; init; }
    public override VisionItemKind Kind => VisionItemKind.Roi;
    public override RotatedRectRoi Region => Roi;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(Geometry(Roi).Concat(OriginalItem?.Fields
        .Where(p => !new[] { "X", "Y", "Width", "Height", "Angle" }.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).Select(p => (p.Key, p.Value)) ?? []).ToArray());
}
public sealed record BlobItem(ItemOrigin Source, BlobResult Blob) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Blob;
    public override RotatedRectRoi Region => Blob.Bounds.ToRotated(Origin.CoordinateSpaceId);
    public override IReadOnlyDictionary<string, CollectionValue> Fields
    {
        get
        {
            var fields = Geometry(Region).Select(p => p.Item1 == "X" ? ("X", (CollectionValue)Blob.CenterX) : p.Item1 == "Y" ? ("Y", (CollectionValue)Blob.CenterY) : p).Concat(new (string, CollectionValue)[] { ("Area", Blob.Area), ("Label", Blob.Label) }).ToList();
            if (Blob.Perimeter is { } p) fields.Add(("Perimeter", p));
            if (Blob.Circularity is { } c) fields.Add(("Circularity", c));
            return Data(fields.ToArray());
        }
    }
}
public sealed record DetectionItem(ItemOrigin Source, YoloDetection Detection) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Detection;
    public override RotatedRectRoi Region => Detection.Bounds.ToRotated(Origin.CoordinateSpaceId);
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(Geometry(Region).Concat(new (string, CollectionValue)[]
        { ("ClassId", Detection.ClassId), ("Class", Detection.Label), ("Confidence", Detection.Confidence) }).ToArray());
}
public sealed record TextItem(ItemOrigin Source, OcrWord Word) : VisionItem(Source)
{
    public BusinessState RecognitionState { get; init; } = BusinessState.Ok;
    public override BusinessState State => RecognitionState;
    public override VisionItemKind Kind => VisionItemKind.Text;
    public override RotatedRectRoi Region => Word.Bounds.ToRotated(Origin.CoordinateSpaceId);
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(Geometry(Region).Concat(new (string, CollectionValue)[]
        { ("Text", Word.Text), ("Confidence", Word.Confidence) }).ToArray());
}
public sealed record CodeItem(ItemOrigin Source, string Text, string Format, IReadOnlyList<Point2D> Points) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Code;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(("Text", Text), ("Format", Format));
}
public sealed record ContourItem(ItemOrigin Source, IReadOnlyList<Point2D> Points, bool Closed) : VisionItem(Source)
{
    public int ParentIndex { get; init; } = -1;
    public bool? IsHole { get; init; }
    public ContourAnalysis? Analysis { get; init; }
    public override RotatedRectRoi? Region => Analysis?.MinimumRectangle;
    public override BusinessState State => Analysis is { Area: null } ? BusinessState.Ng : BusinessState.Ok;
    public override VisionItemKind Kind => VisionItemKind.Contour;
    public override IReadOnlyDictionary<string, CollectionValue> Fields
    {
        get
        {
            var fields = new List<(string, CollectionValue)> { ("PointCount", Points.Count), ("Closed", Closed), ("ParentIndex", ParentIndex) };
            if (IsHole is { } hole) fields.Add(("IsHole", hole));
            if (Analysis is { } a)
            {
                foreach (var (k,v) in new[] { ("Area",a.Area),("SignedArea",a.SignedArea),("Perimeter",a.Perimeter),("Circularity",a.Circularity),("AspectRatio",a.AspectRatio),("Rectangularity",a.Rectangularity) }) if (v is { } n) fields.Add((k,n));
                if (a.MinimumRectangle is { } r) fields.AddRange(Geometry(r));
                if (a.Ellipse is { } e) fields.AddRange(new (string, CollectionValue)[] { ("EllipseWidth",e.Width),("EllipseHeight",e.Height),("EllipseAngle",e.AngleDegrees) });
                if (a.Detail is { } message) fields.Add(("AnalysisDetail",message));
            }
            return Data(fields.ToArray());
        }
    }
}
public sealed record PointSetItem(ItemOrigin Source, IReadOnlyList<Point2D> Points) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.PointSet;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(("PointCount", Points.Count));
}
public sealed record LineItem(ItemOrigin Source, LineGeometry Line) : VisionItem(Source)
{
    public Point2D? EndPoint { get; init; }
    public override VisionItemKind Kind => VisionItemKind.Line;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => EndPoint is { } e ? Data(("X",Line.Origin.X),("Y",Line.Origin.Y),("EndX",e.X),("EndY",e.Y),("Length", Math.Sqrt(Math.Pow(e.X-Line.Origin.X,2)+Math.Pow(e.Y-Line.Origin.Y,2))), ("DirectionX",Line.Direction.X),("DirectionY",Line.Direction.Y)) : Data(("X", Line.Origin.X), ("Y", Line.Origin.Y), ("DirectionX", Line.Direction.X), ("DirectionY", Line.Direction.Y));
}
public sealed record CircleItem(ItemOrigin Source, CircleGeometry Circle) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Circle;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(("X", Circle.Center.X), ("Y", Circle.Center.Y), ("Radius", Circle.Radius));
}
public sealed record MeasurementItem(ItemOrigin Source, MeasurementResult Measurement) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Measurement;
    public override BusinessState State => Measurement.Valid ? BusinessState.Ok : BusinessState.Ng;
    public override IReadOnlyDictionary<string, CollectionValue> Fields => Data(Measurement.Values.Select(p => (p.Key, (CollectionValue)p.Value))
        .Concat(new (string, CollectionValue)[] { ("Valid", Measurement.Valid), ("Unit", Measurement.Unit), ("Kind", Measurement.Kind) }).ToArray());
}
/// <summary>Homogeneous, read-only execution snapshot. Items own no image resources.</summary>
public sealed class VisionCollection
{
    public VisionItemKind Kind { get; }
    public IReadOnlyList<VisionItem> Items { get; }
    public int Count => Items.Count;
    public VisionCollection(VisionItemKind kind, IEnumerable<VisionItem> items)
    {
        var snapshot = items.ToArray();
        if (snapshot.Any(i => i is null || i.Kind != kind)) throw new ArgumentException("Collection items must have the declared kind.");
        Kind = kind; Items = Array.AsReadOnly(snapshot.Select(Freeze).ToArray());
    }
    private VisionCollection(VisionItemKind kind, VisionItem[] trusted) { Kind = kind; Items = Array.AsReadOnly(trusted); }
    public VisionCollection With(IEnumerable<VisionItem> items)
    {
        var selected = items.ToArray();
        if (selected.Any(i => i.Kind != Kind || !Items.Contains(i))) throw new ArgumentException("Selection must reference this immutable snapshot.");
        return new(Kind, selected);
    }
    private static VisionItem Freeze(VisionItem item) => item switch
    {
        RoiItem r => r with { OriginalItem = r.OriginalItem is { } source ? Freeze(source) : null },
        BlobItem b => b with { Blob = b.Blob with { PixelRuns = b.Blob.PixelRuns is null ? null : Array.AsReadOnly(b.Blob.PixelRuns.ToArray()) } },
        ContourItem c => c with { Points = Array.AsReadOnly(c.Points.ToArray()), Analysis = c.Analysis is { } a ? a with { ConvexHull = Array.AsReadOnly(a.ConvexHull.ToArray()), Approximation = Array.AsReadOnly(a.Approximation.ToArray()) } : null },
        PointSetItem p => p with { Points = Array.AsReadOnly(p.Points.ToArray()) },
        CodeItem c => c with { Points = Array.AsReadOnly(c.Points.ToArray()) },
        MeasurementItem m => m with { Measurement = FreezeMeasurement(m.Measurement) },
        IterationItem i => i with { SourceItem = Freeze(i.SourceItem), Result = i.Result is { } r ? r with { Measurement = r.Measurement is { } m ? FreezeMeasurement(m) : null } : null },
        _ => item
    };
    private static MeasurementResult FreezeMeasurement(MeasurementResult m) => m with
    {
        Values = new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(m.Values)),
        Overlays = Array.AsReadOnly(m.Overlays.Select(o => o with { Points = Array.AsReadOnly(o.Points.ToArray()) }).ToArray())
    };
}

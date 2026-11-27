using System.ComponentModel.DataAnnotations;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;

namespace VisionPlatform.Contracts.Collections;

/// <summary>Provenance of a genuine raster boundary. Holds identifiers and bounds only, never the buffer or its lease.</summary>
public sealed record ContourRasterSource(Guid BufferId, RectRoi MaskBounds, RectRoi ExtractionBounds, bool HasHoleHierarchy);

public enum ContourEnvelopeMode
{
    [Display(Name = "轴对齐外接矩形", Description = "按图像坐标轴包住点集，宽高沿用原外接像素框规则。")] AxisAlignedRectangle,
    [Display(Name = "最小面积旋转外接矩形", Description = "可旋转以最小化包围面积，长边作宽，正角顺时针。")] MinimumAreaRectangle,
    [Display(Name = "最小外接圆", Description = "包住所有轮廓点。拟合圆用于描述趋势，可能穿过轮廓。")] MinimumEnclosingCircle,
    [Display(Name = "最大内接圆（像素近似）", Description = "受实际前景、孔洞及凹陷限制。须接入提取轮廓时使用的同一张二值图，并保留孔洞层级。")] MaximumInscribedCircle
}

public enum ContourEnvelopeStatus
{
    Valid, Degenerate, MissingMask, SourceMismatch, IncompleteBoundary, HoleBoundary, OverBudget, EmptyMask, Failed
}

/// <summary>One result per input contour, retaining the original execution and frame association.</summary>
public sealed record ContourEnvelopeItem(ItemOrigin Source, ContourEnvelopeMode Mode, ContourEnvelopeStatus Status,
    RectRoi? AxisAlignedBounds = null, RotatedRectRoi? Rectangle = null, CircleGeometry? Circle = null, string? Reason = null) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.ContourEnvelope;
    public override RotatedRectRoi? Region => Rectangle;
    public override BusinessState State => Status switch
    {
        ContourEnvelopeStatus.Valid => BusinessState.Ok,
        ContourEnvelopeStatus.Degenerate or ContourEnvelopeStatus.HoleBoundary or
            ContourEnvelopeStatus.IncompleteBoundary or ContourEnvelopeStatus.EmptyMask => BusinessState.Ng,
        _ => BusinessState.Error
    };
    public IReadOnlyList<Point2D> Corners => Rectangle?.Corners() ?? [];

    public override IReadOnlyDictionary<string, CollectionValue> Fields
    {
        get
        {
            var values = new List<(string, CollectionValue)>
            {
                ("Method", Mode.ToString()), ("Status", Status.ToString()), ("Reason", Reason ?? ""),
                ("Valid", Status == ContourEnvelopeStatus.Valid)
            };
            if (Rectangle is { } region) values.AddRange(Geometry(region));
            if (AxisAlignedBounds is { } axis)
            {
                values.Add(("Left", axis.X));
                values.Add(("Top", axis.Y));
            }
            if (Circle is { } circle)
                values.AddRange([("X", circle.Center.X), ("Y", circle.Center.Y), ("Radius", circle.Radius), ("Diameter", circle.Radius * 2)]);
            return Data(values.ToArray());
        }
    }
}

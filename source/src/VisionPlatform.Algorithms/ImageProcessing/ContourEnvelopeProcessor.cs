using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenCvSharp;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.ImageProcessing;

public sealed record ContourEnvelopeOptions
{
    [DisplayName("包围几何方式"), Description("外接包住轮廓；内接受实际前景限制；拟合圆和拟合椭圆使用已有拟合算子。尺寸单位为像素。")]
    public ContourEnvelopeMode Mode { get; init; }

    [AppliesWhen(nameof(Mode), nameof(ContourEnvelopeMode.MaximumInscribedCircle))]
    [DisplayName("内接圆单目标像素上限"), Description("按目标包围框加一圈边界计算；每次执行累计上限为 2000 万像素。"), Range(9, 2_000_000)]
    public int MaximumObjectPixels { get; init; } = 2_000_000;
}

public static class ContourEnvelopeProcessor
{
    private const int MaximumContourPoints = 200_000;
    private const int MaximumTotalPoints = 2_000_000;
    private const long MaximumTotalMaskPixels = 20_000_000;

    public static IReadOnlyList<string> Validate(ContourEnvelopeOptions p) =>
        !Enum.IsDefined(p.Mode) || p.Mode == ContourEnvelopeMode.MaximumInscribedCircle && p.MaximumObjectPixels is < 9 or > 2_000_000
            ? ["包围方式无效，或内接圆单目标像素预算不在 9–2000000。"] : [];

    public static VisionCollection Process(VisionCollection contours, ContourEnvelopeOptions options, ImageFrame? mask = null,
        CancellationToken ct = default)
    {
        ProcessingFrame.ThrowInvalid(Validate(options));
        if (contours.Kind != VisionItemKind.Contour) throw new ArgumentException("轮廓包围几何需要轮廓集合；Blob 边界框不能代替轮廓。");
        if (contours.Count > 10_000) throw new ArgumentException("最多处理 10000 个轮廓，请先筛选集合。");
        var output = new List<ContourEnvelopeItem>(contours.Count);
        long maskPixels = 0, pointCount = 0;
        foreach (var contour in contours.Items.Cast<ContourItem>())
        {
            ct.ThrowIfCancellationRequested();
            pointCount += contour.Points.Count;
            output.Add(pointCount > MaximumTotalPoints
                ? new(contour.Origin, options.Mode, ContourEnvelopeStatus.OverBudget, Reason: "轮廓点累计超过 200 万，请筛选后再测量。")
                : Measure(contour, options, mask, ref maskPixels, ct));
        }
        return new(VisionItemKind.ContourEnvelope, output);
    }

    private static ContourEnvelopeItem Measure(ContourItem item, ContourEnvelopeOptions options, ImageFrame? mask,
        ref long maskPixels, CancellationToken ct)
    {
        var mode = options.Mode;
        ContourEnvelopeItem Reject(ContourEnvelopeStatus status, string reason) => new(item.Origin, mode, status, Reason: reason);
        if (item.Points.Count > MaximumContourPoints) return Reject(ContourEnvelopeStatus.OverBudget, "单个轮廓最多允许 20 万个点。");
        if (!item.Closed || item.Points.Count < 3) return Reject(ContourEnvelopeStatus.Degenerate, "需要闭合轮廓及至少三个非共线点。");
        var offset = item.Points[0];
        var points = new Point2f[item.Points.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var point = item.Points[i];
            var x = point.X - offset.X;
            var y = point.Y - offset.Y;
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 16_000_000 || Math.Abs(y) > 16_000_000)
                return Reject(ContourEnvelopeStatus.Degenerate, "轮廓有非有限坐标或点跨度超出可计算范围。");
            points[i] = new((float)x, (float)y);
        }
        try
        {
            if (Math.Abs(Cv2.ContourArea(points)) < 1e-8)
                return Reject(ContourEnvelopeStatus.Degenerate, "轮廓面积为零或退化。");
            ct.ThrowIfCancellationRequested();
            var space = item.Origin.CoordinateSpaceId;
            switch (mode)
            {
                case ContourEnvelopeMode.AxisAlignedRectangle:
                    var bounds = ShapeProcessors.AxisAlignedRectangle(points, offset);
                    return new(item.Origin, mode, ContourEnvelopeStatus.Valid, bounds, bounds.ToRotated(space));
                case ContourEnvelopeMode.MinimumAreaRectangle:
                    var rect = ShapeProcessors.MinimumRectangle(points, offset, space);
                    return rect is not null ? new(item.Origin, mode, ContourEnvelopeStatus.Valid, Rectangle: rect)
                        : Reject(ContourEnvelopeStatus.Degenerate, "旋转矩形宽或高退化。");
                case ContourEnvelopeMode.MaximumInscribedCircle:
                    return Inscribed(item, mask, options.MaximumObjectPixels, ref maskPixels, ct);
                default:
                    Cv2.MinEnclosingCircle(points, out var center, out _);
                    var global = new Point2D(center.X + offset.X, center.Y + offset.Y);
                    // Recheck against the original double-precision points so the Float32 center cannot exclude a boundary point.
                    var radius = item.Points.Max(q => double.Hypot(q.X - global.X, q.Y - global.Y));
                    return new(item.Origin, mode, ContourEnvelopeStatus.Valid, Circle: new(global, radius, space));
            }
        }
        catch (OpenCVException ex) { return Reject(ContourEnvelopeStatus.Failed, "OpenCV 无法计算轮廓几何：" + ex.Message); }
    }

    private static ContourEnvelopeItem Inscribed(ContourItem item, ImageFrame? mask, int maximumPixels, ref long consumed, CancellationToken ct)
    {
        const ContourEnvelopeMode mode = ContourEnvelopeMode.MaximumInscribedCircle;
        ContourEnvelopeItem Reject(ContourEnvelopeStatus status, string reason) => new(item.Origin, mode, status, Reason: reason);
        if (mask is null) return Reject(ContourEnvelopeStatus.MissingMask, "请连接提取轮廓时使用的同一张 Mono8 0/255 二值图。");
        if (item.RasterSource is not { HasHoleHierarchy: true } source || item.IsHole is null)
            return Reject(ContourEnvelopeStatus.IncompleteBoundary, "找不到原始轮廓或完整孔洞层级。请使用树形或两层检索，并连接原始轮廓集合。");
        if (item.IsHole == true) return Reject(ContourEnvelopeStatus.HoleBoundary, "此项是孔洞边界，不是前景连通目标。");
        if (mask.PixelFormat != PixelFormat.Mono8 || item.Origin.FrameId != mask.FrameId ||
            item.Origin.CoordinateSpaceId != mask.CoordinateSpace.Id || source.BufferId != mask.BufferHandle.BufferId ||
            source.MaskBounds != ShapeProcessors.RasterBounds(mask))
            return Reject(ContourEnvelopeStatus.SourceMismatch, "二值图的缓冲身份、帧、尺寸或坐标与提取轮廓时不一致。");

        var left = item.Points.Min(p => p.X);
        var top = item.Points.Min(p => p.Y);
        var right = item.Points.Max(p => p.X);
        var bottom = item.Points.Max(p => p.Y);
        var extraction = source.ExtractionBounds;
        if (left < extraction.X + 1 || top < extraction.Y + 1 ||
            right >= extraction.X + extraction.Width - 1 || bottom >= extraction.Y + extraction.Height - 1)
            return Reject(ContourEnvelopeStatus.IncompleteBoundary, "该目标接触图像或轮廓提取 ROI 边缘，范围可能被截断。");

        var x = (int)Math.Round(left - source.MaskBounds.X) - 1;
        var y = (int)Math.Round(top - source.MaskBounds.Y) - 1;
        var width = (int)Math.Round(right - left) + 3;
        var height = (int)Math.Round(bottom - top) + 3;
        var budget = (long)width * height;
        if (budget > maximumPixels || consumed + budget > MaximumTotalMaskPixels)
            return Reject(ContourEnvelopeStatus.OverBudget, "该目标局部掩膜或累计掩膜面积超过预算。");
        consumed += budget;

        using var view = mask.CreateView(x, y, width, height);
        using var borrowed = new OpenCvImageLease(view);
        var seed = new Point((int)Math.Round(item.Points[0].X - view.CoordinateSpace.OriginX),
            (int)Math.Round(item.Points[0].Y - view.CoordinateSpace.OriginY));
        if (borrowed.Mat.At<byte>(seed.Y, seed.X) != 255)
            return Reject(ContourEnvelopeStatus.EmptyMask, "轮廓起点没有有效前景。");

        // Fill only this foreground component. In an annulus the hole stays zero; a separate island also stays excluded.
        using var flood = borrowed.Mat.Clone();
        if (Cv2.FloodFill(flood, seed, Scalar.All(127), out var component, flags: FloodFillFlags.Link8) == 0)
            return Reject(ContourEnvelopeStatus.EmptyMask, "掩膜中没有该轮廓对应的连通前景。");
        if (component != new Rect(1, 1, width - 2, height - 2))
            return Reject(ContourEnvelopeStatus.IncompleteBoundary, "连通前景的范围与原始轮廓不一致，不能作为完整目标测量。");
        ct.ThrowIfCancellationRequested();
        using var foreground = new Mat();
        using var distance = new Mat();
        Cv2.Compare(flood, Scalar.All(127), foreground, CmpTypes.EQ);
        Cv2.DistanceTransform(foreground, distance, DistanceTypes.L2, DistanceTransformMasks.Precise);
        Cv2.MinMaxLoc(distance, out _, out var centerDistance, out _, out var peak);
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(centerDistance) || centerDistance <= 0)
            return Reject(ContourEnvelopeStatus.EmptyMask, "未找到可容纳正半径圆的有效前景中心。");

        // Distance transform selects the center. Measure to the actual closed pixel squares at that center,
        // so the circle cannot cut through a background cell even along a diagonal or a hole.
        var radius = RadiusAt(peak, foreground, ct);
        var center = new Point2D(view.CoordinateSpace.OriginX + peak.X, view.CoordinateSpace.OriginY + peak.Y);
        return new(item.Origin, mode, ContourEnvelopeStatus.Valid, Circle: new CircleGeometry(center, radius, item.Origin.CoordinateSpaceId));
    }

    private static unsafe double RadiusAt(Point center, Mat foreground, CancellationToken ct)
    {
        var minimumSquared = double.PositiveInfinity;
        var height = foreground.Height;
        var width = foreground.Width;
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            var dy = Math.Max(0, Math.Abs(center.Y - y) - 0.5);
            if (dy * dy >= minimumSquared) continue;
            var row = new ReadOnlySpan<byte>((void*)foreground.Ptr(y), width);
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x] != 0) continue;
                var dx = Math.Max(0, Math.Abs(center.X - x) - 0.5);
                minimumSquared = Math.Min(minimumSquared, dx * dx + dy * dy);
            }
        }
        return Math.BitDecrement(Math.Sqrt(minimumSquared));
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Algorithms.Blob;

public sealed record BlobRangeOptions
{
    [DisplayName("启用条件")]
    public bool Enabled { get; init; }
    [DisplayName("最小值（包含）")]
    public double Minimum { get; init; }
    [DisplayName("最大值（包含）")]
    public double Maximum { get; init; } = int.MaxValue;
}

public enum BlobSortOrder { None, AreaAscending, AreaDescending, XAscending, YAscending }
public sealed record BlobFilterOptions
{
    [DisplayName("面积筛选")]
    public BlobRangeOptions Area { get; init; } = new();
    [DisplayName("宽度筛选")]
    public BlobRangeOptions Width { get; init; } = new();
    [DisplayName("高度筛选")]
    public BlobRangeOptions Height { get; init; } = new();
    [DisplayName("长宽比筛选（宽/高）")]
    public BlobRangeOptions AspectRatio { get; init; } = new();
    [DisplayName("圆度筛选（4邻域数字周长）")]
    [Description("圆度 = 4π×面积/周长²，周长统计外露像素边和孔洞边，不是光滑轮廓估计。")]
    public BlobRangeOptions Circularity { get; init; } = new() { Maximum = 1 };
    [DisplayName("中心 X 筛选")]
    public BlobRangeOptions CenterX { get; init; } = new() { Minimum = -int.MaxValue };
    [DisplayName("中心 Y 筛选")]
    public BlobRangeOptions CenterY { get; init; } = new() { Minimum = -int.MaxValue };
    [DisplayName("排序方式")]
    public BlobSortOrder Sort { get; init; }
    [DisplayName("最大保留数量"), Range(0, int.MaxValue)]
    public int MaximumCount { get; init; } = int.MaxValue;
}

/// <summary>Caller owns FilteredMask; measurements and runs are immutable borrowed data.</summary>
public sealed record BlobFilterResult(BlobResults FilteredBlobs, ImageFrame FilteredMask,
    RectRoi[] BoundingBoxes, Point2D[] Centroids) : IDisposable
{
    public int BlobCount => FilteredBlobs.Count;
    public void Dispose() => FilteredMask.Dispose();
}

public static class BlobFilter
{
    public static IReadOnlyList<string> Validate(BlobFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        foreach (var (name, range, nonnegative) in Ranges(options))
        {
            if (range is null) { errors.Add(name + " must contain filter parameters."); continue; }
            if (!range.Enabled) continue;
            if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum) || range.Minimum > range.Maximum ||
                (nonnegative && range.Minimum < 0) || (name == nameof(options.Circularity) && range.Maximum > 1))
                errors.Add(name + " requires a finite ordered range" + (nonnegative ? " with nonnegative bounds." : "."));
        }
        if (!Enum.IsDefined(options.Sort)) errors.Add("Sort is invalid.");
        if (options.MaximumCount < 0) errors.Add("MaximumCount must be nonnegative.");
        return errors;
    }

    public static BlobFilterResult Apply(BlobResults input, BlobFilterOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = Validate(options);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(options));
        if (input.RasterWidth <= 0 || input.RasterHeight <= 0 || !double.IsFinite(input.RasterOriginX) ||
            !double.IsFinite(input.RasterOriginY) || string.IsNullOrWhiteSpace(input.CoordinateSpaceId))
            throw new ArgumentException("Blob raster metadata is missing. Re-run BlobDetection to obtain exact foreground pixels.", nameof(input));
        ct.ThrowIfCancellationRequested();
        var kept = new List<BlobResult>();
        foreach (var blob in input.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (!Fits(options.Area, blob.Area) || !Fits(options.Width, blob.Bounds.Width) || !Fits(options.Height, blob.Bounds.Height) ||
                !Fits(options.AspectRatio, blob.Bounds.Width / blob.Bounds.Height) || !Fits(options.CenterX, blob.CenterX) || !Fits(options.CenterY, blob.CenterY)) continue;
            if (options.Circularity.Enabled)
            {
                if (blob.Circularity is not { } circularity || !double.IsFinite(circularity))
                    throw new ArgumentException("Circularity metadata is missing. Re-run BlobDetection.", nameof(input));
                if (!Fits(options.Circularity, circularity)) continue;
            }
            kept.Add(blob);
        }
        IEnumerable<BlobResult> ordered = options.Sort switch
        {
            BlobSortOrder.AreaAscending => kept.OrderBy(b => b.Area).ThenBy(b => b.Label),
            BlobSortOrder.AreaDescending => kept.OrderByDescending(b => b.Area).ThenBy(b => b.Label),
            BlobSortOrder.XAscending => kept.OrderBy(b => b.CenterX).ThenBy(b => b.CenterY).ThenBy(b => b.Label),
            BlobSortOrder.YAscending => kept.OrderBy(b => b.CenterY).ThenBy(b => b.CenterX).ThenBy(b => b.Label),
            _ => kept
        };
        var selected = ordered.Take(options.MaximumCount).ToArray();
        using var mask = new ImageFrameBuilder(input.RasterWidth, input.RasterHeight, PixelFormat.Mono8,
            coordinateSpace: new ImageCoordinateSpace(input.CoordinateSpaceId, input.RasterOriginX, input.RasterOriginY));
        // Builder memory is zero-initialized. No previous run can leak into an empty result.
        foreach (var blob in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (blob.PixelRuns is not { } runs)
                throw new ArgumentException("Blob pixel runs are missing. Exact FilteredMask cannot be fabricated from bounding boxes.", nameof(input));
            long area = 0; var previousY = -1; var previousEndX = 0;
            foreach (var run in runs)
            {
                ct.ThrowIfCancellationRequested();
                if (run.Length <= 0 || run.X < 0 || run.Y < 0 || run.Y >= input.RasterHeight ||
                    (long)run.X + run.Length > input.RasterWidth || run.Y < previousY ||
                    (run.Y == previousY && run.X < previousEndX))
                    throw new ArgumentException("Blob pixel runs contain invalid or overlapping raster coordinates.", nameof(input));
                mask.GetRowSpan(run.Y).Slice(run.X, run.Length).Fill(255);
                previousY = run.Y; previousEndX = run.X + run.Length; area += run.Length;
            }
            if (area != blob.Area) throw new ArgumentException("Blob pixel runs do not agree with measured area.", nameof(input));
        }
        ct.ThrowIfCancellationRequested();
        var filtered = input with { Items = Array.AsReadOnly(selected) };
        return new(filtered, mask.Build(), selected.Select(b => b.Bounds).ToArray(), selected.Select(b => new Point2D(b.CenterX, b.CenterY)).ToArray());
    }

    private static bool Fits(BlobRangeOptions range, double value) => !range.Enabled ||
        double.IsFinite(value) && value >= range.Minimum && value <= range.Maximum;

    private static IEnumerable<(string Name, BlobRangeOptions? Range, bool Nonnegative)> Ranges(BlobFilterOptions p)
    {
        yield return (nameof(p.Area), p.Area, true); yield return (nameof(p.Width), p.Width, true);
        yield return (nameof(p.Height), p.Height, true); yield return (nameof(p.AspectRatio), p.AspectRatio, true);
        yield return (nameof(p.Circularity), p.Circularity, true); yield return (nameof(p.CenterX), p.CenterX, false);
        yield return (nameof(p.CenterY), p.CenterY, false);
    }
}

using OpenCvSharp;
using VisionPlatform.Algorithms.ROI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Blob;

public sealed record BlobOptions
{
    public byte Threshold { get; init; } = 180;
    public int MinArea { get; init; } = 3;
    public int MaxArea { get; init; } = int.MaxValue;
    public bool DarkForeground { get; init; }
}

public static class BlobDetector
{
    /// <summary>OpenCV connected components with an actual rotated ROI mask. Centers and bounding boxes use original image coordinates.</summary>
    public static BlobResults Detect(ImageFrame image, RotatedRectRoi? roi, BlobOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(options);
        if (options.MinArea < 1 || options.MaxArea < options.MinArea) throw new ArgumentException("Blob areas must satisfy 1 <= MinArea <= MaxArea.", nameof(options));
        if (image.PixelFormat.IsBayer()) throw new NotSupportedException("Bayer input requires explicit RGB8/BGR8 conversion before Blob detection.");
        ct.ThrowIfCancellationRequested();
        roi ??= new RotatedRectRoi(image.CoordinateSpace.OriginX + image.Width / 2.0,
            image.CoordinateSpace.OriginY + image.Height / 2.0, image.Width, image.Height, 0, image.CoordinateSpace.Id);
        RoiTransformer.ValidateInImage(roi, image);
        var points = roi.Corners();
        var left = Math.Max(0, (int)Math.Floor(points.Min(p => p.X) - image.CoordinateSpace.OriginX + 1e-9));
        var top = Math.Max(0, (int)Math.Floor(points.Min(p => p.Y) - image.CoordinateSpace.OriginY + 1e-9));
        var right = Math.Min(image.Width, (int)Math.Ceiling(points.Max(p => p.X) - image.CoordinateSpace.OriginX - 1e-9));
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(points.Max(p => p.Y) - image.CoordinateSpace.OriginY - 1e-9));
        using var view = image.CreateView(left, top, right - left, bottom - top);
        using var gray = PixelFormatConverter.Convert(view, PixelFormat.Mono8);
        using var input = new OpenCvImageLease(gray.Image);
        using var binary = new Mat();
        Cv2.Threshold(input.Mat, binary, options.Threshold, 255, options.DarkForeground ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
        using var mask = CreateMask(roi, view, ct);
        Cv2.BitwiseAnd(binary, mask, binary);
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var result = new List<BlobResult>();
        for (var label = 1; label < count; label++)
        {
            ct.ThrowIfCancellationRequested();
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < options.MinArea || area > options.MaxArea) continue;
            var bounds = new RectRoi(view.CoordinateSpace.OriginX + stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                view.CoordinateSpace.OriginY + stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(label, (int)ConnectedComponentsTypes.Width), stats.At<int>(label, (int)ConnectedComponentsTypes.Height));
            result.Add(new BlobResult(label, area, view.CoordinateSpace.OriginX + centroids.At<double>(label, 0),
                view.CoordinateSpace.OriginY + centroids.At<double>(label, 1), bounds));
        }
        // Enrich the same selected labels without changing original area/centroid/bounds semantics.
        // Runs are input-image local, not cropped ROI-view local. Four-neighbor digital perimeter
        // counts exposed unit pixel edges (including holes), giving a reproducible circularity.
        var metadata = MeasureLabels(labels, result.Select(b => b.Label), left, top, ct);
        ct.ThrowIfCancellationRequested();
        var measured = result.Select(b => b with
        {
            Perimeter = metadata[b.Label].Perimeter,
            PixelRuns = Array.AsReadOnly(metadata[b.Label].Runs.ToArray())
        }).OrderByDescending(b => b.Area).ThenBy(b => b.CenterY).ThenBy(b => b.CenterX).ToArray();
        return new BlobResults(measured, image.CoordinateSpace.Id)
        {
            RasterWidth = image.Width, RasterHeight = image.Height,
            RasterOriginX = image.CoordinateSpace.OriginX, RasterOriginY = image.CoordinateSpace.OriginY
        };
    }

    private sealed class LabelMeasurement
    {
        public List<BlobPixelRun> Runs { get; } = [];
        public double Perimeter { get; set; }
    }

    private static unsafe Dictionary<int, LabelMeasurement> MeasureLabels(Mat labels, IEnumerable<int> selected,
        int offsetX, int offsetY, CancellationToken ct)
    {
        var result = selected.ToDictionary(label => label, _ => new LabelMeasurement());
        if (result.Count == 0) return result;
        var rows = labels.Rows; var columns = labels.Cols;
        for (var y = 0; y < rows; y++)
        {
            ct.ThrowIfCancellationRequested();
            var row = new ReadOnlySpan<int>((void*)labels.Ptr(y), columns);
            var above = y > 0 ? new ReadOnlySpan<int>((void*)labels.Ptr(y - 1), columns) : default;
            var below = y + 1 < rows ? new ReadOnlySpan<int>((void*)labels.Ptr(y + 1), columns) : default;
            for (var x = 0; x < row.Length;)
            {
                var label = row[x]; var start = x;
                while (x < row.Length && row[x] == label) x++;
                if (!result.TryGetValue(label, out var measurement)) continue;
                measurement.Runs.Add(new BlobPixelRun(offsetX + start, offsetY + y, x - start));
                measurement.Perimeter += 2; // left/right ends of this maximal run
                for (var xx = start; xx < x; xx++)
                {
                    if (above.IsEmpty || above[xx] != label) measurement.Perimeter++;
                    if (below.IsEmpty || below[xx] != label) measurement.Perimeter++;
                }
            }
        }
        return result;
    }

    private static unsafe Mat CreateMask(RotatedRectRoi roi, ImageFrame view, CancellationToken ct)
    {
        var mask = new Mat(view.Height, view.Width, MatType.CV_8UC1);
        try
        {
            var radians = roi.AngleDegrees * Math.PI / 180;
            var c = Math.Cos(radians); var s = Math.Sin(radians);
            for (var y = 0; y < view.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                var row = new Span<byte>((void*)mask.Ptr(y), view.Width);
                var dy = view.CoordinateSpace.OriginY + y + 0.5 - roi.CenterY;
                for (var x = 0; x < view.Width; x++)
                {
                    var dx = view.CoordinateSpace.OriginX + x + 0.5 - roi.CenterX;
                    // Pixel-center inclusion gives a defined rasterization, including rotated boundary pixels.
                    row[x] = Math.Abs(c * dx + s * dy) <= roi.Width / 2 && Math.Abs(-s * dx + c * dy) <= roi.Height / 2 ? (byte)255 : (byte)0;
                }
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }
}

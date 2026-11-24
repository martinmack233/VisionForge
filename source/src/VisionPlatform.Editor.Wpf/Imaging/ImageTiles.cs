using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;
using PixelFormatConverter = VisionPlatform.Imaging.PixelFormatConverter;

namespace VisionPlatform.Editor.Wpf.Imaging;

public sealed record VisibleImageTile(RectRoi Region, int PyramidLevel, ImagePreview Preview);
internal sealed record TileRequest(RectRoi Region, int Level);

/// <summary>Reads only a requested raster region. Level zero preserves every original display sample.
/// Bayer is bilinear demosaiced with a two-pixel halo before the core is extracted.</summary>
public sealed class OpenCvImageTileProvider : IImageTileProvider
{
    private readonly SemaphoreSlim _workers;
    public int MaximumConcurrency { get; }
    public OpenCvImageTileProvider(int maximumConcurrency = 2)
    {
        if (maximumConcurrency is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        MaximumConcurrency = maximumConcurrency; _workers = new(maximumConcurrency, maximumConcurrency);
    }

    public async Task<ImagePreview> RequestTileAsync(ImageFrame source, RectRoi imageRegion, int pyramidLevel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(imageRegion);
        using var retained = source.Share();
        var entered = false;
        try
        {
            await _workers.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            // Do not pass the token to Task.Run: the delegate owns required cleanup even on pre-cancellation.
            return await Task.Run(() => Create(retained, imageRegion, pyramidLevel, cancellationToken)).ConfigureAwait(false);
        }
        finally { if (entered) _workers.Release(); }
    }

    private static ImagePreview Create(ImageFrame image, RectRoi region, int level, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (level is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(level));
        var x = Integer(region.X - image.CoordinateSpace.OriginX); var y = Integer(region.Y - image.CoordinateSpace.OriginY);
        var width = Integer(region.Width); var height = Integer(region.Height);
        if (x < 0 || y < 0 || width < 1 || height < 1 || (long)x + width > image.Width || (long)y + height > image.Height)
            throw new ArgumentOutOfRangeException(nameof(region), "Tile region must lie within the image.");
        var factor = 1 << level;
        var outputWidth = checked((int)(((long)width + factor - 1) / factor));
        var outputHeight = checked((int)(((long)height + factor - 1) / factor));
        if (outputWidth > 512 || outputHeight > 512)
            throw new ArgumentException("Tile output must be at most 512 by 512 samples; split larger requests.", nameof(region));
        var channels = image.PixelFormat == PixelFormat.Mono8 ? 1 : 3;
        var stride = checked(outputWidth * channels);
        var bytes = new byte[checked(stride * outputHeight)];
        if (level == 0)
        {
            if (image.PixelFormat.IsBayer())
            {
                using var color = DemosaicCore(image, x, y, width, height);
                CopyMat(color, bytes, stride, height, ct);
            }
            else
            {
                using var view = image.CreateView(x, y, width, height); using var lease = view.Lease();
                for (var row = 0; row < height; row++)
                {
                    ct.ThrowIfCancellationRequested();
                    lease.GetRowSpan(row).CopyTo(bytes.AsSpan(row * stride, stride));
                }
                if (image.PixelFormat == PixelFormat.RGB8)
                    for (var offset = 0; offset < bytes.Length; offset += 3) (bytes[offset], bytes[offset + 2]) = (bytes[offset + 2], bytes[offset]);
            }
        }
        else if (image.PixelFormat.IsBayer())
        {
            // At low zoom a tile can cover most of the image. Demosaic bounded source-row stripes,
            // never a complete high-resolution colour frame. Weighted stripe means bound staging
            // independently of pyramid factor, including levels with >128 source rows per sample.
            for (var outputY = 0; outputY < outputHeight; outputY++)
            {
                ct.ThrowIfCancellationRequested();
                var sourceY = checked(y + outputY * factor);
                var sourceEnd = (int)Math.Min((long)y + height, (long)sourceY + factor);
                var sums = new double[stride]; var totalRows = sourceEnd - sourceY;
                for (var stripeY = sourceY; stripeY < sourceEnd; stripeY += 128)
                {
                    ct.ThrowIfCancellationRequested(); var rows = Math.Min(128, sourceEnd - stripeY);
                    using var core = DemosaicCore(image, x, stripeY, width, rows);
                    using var mean = new Mat();
                    Cv2.Resize(core, mean, new Size(outputWidth, 1), 0, 0, InterpolationFlags.Area);
                    var samples = new byte[stride]; Marshal.Copy(mean.Data, samples, 0, stride);
                    for (var i = 0; i < stride; i++) sums[i] += samples[i] * rows;
                }
                for (var i = 0; i < stride; i++) bytes[outputY * stride + i] = (byte)Math.Clamp((int)Math.Round(sums[i] / totalRows), 0, 255);
            }
        }
        else
        {
            using var view = image.CreateView(x, y, width, height); using var lease = new OpenCvImageLease(view);
            using var resized = new Mat();
            Cv2.Resize(lease.Mat, resized, new Size(outputWidth, outputHeight), 0, 0, InterpolationFlags.Area);
            if (image.PixelFormat == PixelFormat.RGB8) Cv2.CvtColor(resized, resized, ColorConversionCodes.RGB2BGR);
            CopyMat(resized, bytes, stride, outputHeight, ct);
        }
        ct.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(outputWidth, outputHeight, 96, 96,
            channels == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, bytes, stride);
        bitmap.Freeze(); return new(bitmap, bytes.LongLength, width, height);
    }

    private static Mat DemosaicCore(ImageFrame image, int x, int y, int width, int height)
    {
        var left = Math.Max(0, x - 2); var top = Math.Max(0, y - 2);
        var right = (int)Math.Min(image.Width, (long)x + width + 2); var bottom = (int)Math.Min(image.Height, (long)y + height + 2);
        using var expanded = image.CreateView(left, top, right - left, bottom - top);
        using var color = PixelFormatConverter.Convert(expanded, PixelFormat.BGR8);
        using var lease = new OpenCvImageLease(color.Image);
        using var core = new Mat(lease.Mat, new Rect(x - left, y - top, width, height));
        return core.Clone(); // Only a bounded tile/stripe core, never the full source image.
    }
    private static void CopyMat(Mat mat, byte[] bytes, int stride, int height, CancellationToken ct)
    { for (var y = 0; y < height; y++) { ct.ThrowIfCancellationRequested(); Marshal.Copy(mat.Ptr(y), bytes, y * stride, stride); } }
    private static int Integer(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue || Math.Abs(value - Math.Round(value)) > 1e-8)
            throw new ArgumentException("Tile coordinates must have integer raster bounds relative to the input.");
        return checked((int)Math.Round(value));
    }
}

/// <summary>LRU owns frozen bitmap payload only. Keys retain no ImageFrame lease; clear invalidates in-flight inserts.</summary>
public sealed class ImageTileCache : IDisposable
{
    private sealed record Key(FrameHandle Buffer, int Width, int Height, int Stride, PixelFormat Format,
        double OriginX, double OriginY, RectRoi Region, int Level);
    private readonly object _gate = new();
    private readonly IImageTileProvider _provider;
    private readonly Dictionary<Key, LinkedListNode<(Key Key, ImagePreview Preview)>> _items = new();
    private readonly LinkedList<(Key Key, ImagePreview Preview)> _lru = new();
    private long _bytes, _epoch, _requests, _hits; private bool _disposed;
    public long MaximumBytes { get; }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _items.Count; } }
    public long Requests => Interlocked.Read(ref _requests);
    public long Hits => Interlocked.Read(ref _hits);
    public ImageTileCache(long maximumBytes = 48L * 1024 * 1024, IImageTileProvider? provider = null)
    { if (maximumBytes < 3L * 512 * 512) throw new ArgumentOutOfRangeException(nameof(maximumBytes)); MaximumBytes = maximumBytes; _provider = provider ?? new OpenCvImageTileProvider(); }
    private static Key MakeKey(ImageFrame source, RectRoi region, int level) => new(source.BufferHandle, source.Width, source.Height,
        source.Stride, source.PixelFormat, source.CoordinateSpace.OriginX, source.CoordinateSpace.OriginY, region, level);
    public bool TryGet(ImageFrame source, RectRoi region, int level, out ImagePreview? preview)
    {
        var key = MakeKey(source, region, level);
        lock (_gate)
        {
            preview = null; if (_disposed || !_items.TryGetValue(key, out var item)) return false;
            _lru.Remove(item); _lru.AddFirst(item); preview = item.Value.Preview; return true;
        }
    }
    public async Task GetAsync(ImageFrame source, RectRoi region, int level, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var key = MakeKey(source, region, level); long epoch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); epoch = _epoch;
            if (_items.TryGetValue(key, out var item)) { _lru.Remove(item); _lru.AddFirst(item); Interlocked.Increment(ref _hits); return; }
        }
        Interlocked.Increment(ref _requests);
        var result = await _provider.RequestTileAsync(source, region, level, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || epoch != _epoch) return;
            if (_items.ContainsKey(key)) return;
            if (result.Bytes > MaximumBytes) throw new InvalidOperationException("One tile exceeds its cache budget.");
            while (_bytes + result.Bytes > MaximumBytes && _lru.Last is { } oldest)
            { _items.Remove(oldest.Value.Key); _bytes -= oldest.Value.Preview.Bytes; _lru.RemoveLast(); }
            var node = _lru.AddFirst((key, result)); _items.Add(key, node); _bytes += result.Bytes;
        }
    }
    public void Clear() { lock (_gate) { _epoch++; _items.Clear(); _lru.Clear(); _bytes = 0; } }
    public void Dispose() { lock (_gate) { _disposed = true; _epoch++; _items.Clear(); _lru.Clear(); _bytes = 0; } }
}

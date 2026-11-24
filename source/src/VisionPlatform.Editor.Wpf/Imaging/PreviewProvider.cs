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

public sealed record ImagePreview(BitmapSource Bitmap, long Bytes, int SourceWidth, int SourceHeight)
{
    public bool IsThumbnail => Bitmap.PixelWidth != SourceWidth || Bitmap.PixelHeight != SourceHeight;
}
public interface IPreviewProvider
{
    Task<ImagePreview> CreateAsync(ImageFrame source, int maximumSide, long maximumBytes, CancellationToken cancellationToken);
}
/// <summary>Region in original image coordinates; level zero is the original raster, level L downsamples by 2^L.</summary>
public interface IImageTileProvider
{
    Task<ImagePreview> RequestTileAsync(ImageFrame source, RectRoi imageRegion, int pyramidLevel, CancellationToken cancellationToken);
}

public sealed class OpenCvPreviewProvider : IPreviewProvider
{
    private readonly SemaphoreSlim _worker = new(1, 1);
    public Task<ImagePreview> CreateAsync(ImageFrame source, int maximumSide, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumSide < 1 || maximumBytes < 4) throw new ArgumentOutOfRangeException(nameof(maximumSide));
        var retained = source.Share();
        return CreateBoundedAsync(retained, maximumSide, maximumBytes, cancellationToken);
    }
    private async Task<ImagePreview> CreateBoundedAsync(ImageFrame retained, int maximumSide, long maximumBytes, CancellationToken ct)
    {
        using (retained)
        {
            var entered = false;
            try
            {
                await _worker.WaitAsync(ct).ConfigureAwait(false); entered = true;
        // The delegate always executes and releases its share, even when the token was cancelled before scheduling.
                return await Task.Run(() => Create(retained, maximumSide, maximumBytes, ct)).ConfigureAwait(false);
            }
            finally { if (entered) _worker.Release(); }
        }
    }

    private static ImagePreview Create(ImageFrame image, int maximumSide, long maximumBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var channels = image.PixelFormat == PixelFormat.Mono8 ? 1 : 3;
        var scale = Math.Min(1, (double)maximumSide / Math.Max(image.Width, image.Height));
        scale = Math.Min(scale, Math.Sqrt((double)maximumBytes / ((double)image.Width * image.Height * channels)));
        var width = Math.Max(1, (int)Math.Floor(image.Width * scale));
        var height = Math.Max(1, (int)Math.Floor(image.Height * scale));
        // Clamping a thin image dimension to one pixel can defeat the continuous-area estimate above.
        // Enforce the discrete pixel budget before allocating either the native thumbnail or BitmapSource.
        var pixelBudget = maximumBytes / channels;
        if ((long)width * height > pixelBudget)
        {
            if (width >= height) width = checked((int)Math.Max(1, pixelBudget / height));
            else height = checked((int)Math.Max(1, pixelBudget / width));
        }
        using var thumbnail = new Mat(height, width, channels == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3);
        if (image.PixelFormat.IsBayer())
        {
            // Decode bounded stripes with their physical Bayer phase preserved. Never allocate a whole color copy
            // of an 82MP mosaic merely to display a thumbnail. This resampling is preview-only.
            const int outputRowsPerStripe = 32;
            for (var outputY = 0; outputY < height; outputY += outputRowsPerStripe)
            {
                ct.ThrowIfCancellationRequested();
                var rows = Math.Min(outputRowsPerStripe, height - outputY);
                var sourceY = (int)((long)outputY * image.Height / height);
                var sourceEnd = (int)((long)(outputY + rows) * image.Height / height);
                var top = Math.Max(0, sourceY - 2); var bottom = Math.Min(image.Height, sourceEnd + 2);
                using var stripe = image.CreateView(0, top, image.Width, bottom - top);
                using var converted = PixelFormatConverter.Convert(stripe, PixelFormat.BGR8);
                using var pixels = new OpenCvImageLease(converted.Image);
                using var useful = new Mat(pixels.Mat, new Rect(0, sourceY - top, image.Width, Math.Max(1, sourceEnd - sourceY)));
                using var destination = new Mat(thumbnail, new Rect(0, outputY, width, rows));
                Cv2.Resize(useful, destination, new Size(width, rows), 0, 0, InterpolationFlags.Area);
            }
        }
        else
        {
            using var input = new OpenCvImageLease(image);
            Cv2.Resize(input.Mat, thumbnail, new Size(width, height), 0, 0, InterpolationFlags.Area);
            if (image.PixelFormat == PixelFormat.RGB8) Cv2.CvtColor(thumbnail, thumbnail, ColorConversionCodes.RGB2BGR);
        }
        ct.ThrowIfCancellationRequested();
        var stride = checked(width * channels);
        var bytes = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++) Marshal.Copy(thumbnail.Ptr(y), bytes, y * stride, stride);
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            channels == 1 ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, bytes, stride);
        bitmap.Freeze();
        return new(bitmap, bytes.LongLength, image.Width, image.Height);
    }
}

/// <summary>Byte-budgeted LRU of frozen previews. Cache entries own no ImageFrame or native pixel lease.</summary>
public sealed class PreviewCache : IDisposable
{
    private readonly IPreviewProvider _provider;
    private readonly object _gate = new();
    private readonly LinkedList<(PreviewKey Key, ImagePreview Preview)> _lru = new();
    private readonly Dictionary<PreviewKey, LinkedListNode<(PreviewKey Key, ImagePreview Preview)>> _entries = new();
    private long _bytes, _epoch;
    private bool _disposed;
    public long MaximumBytes { get; }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public PreviewCache(long maximumBytes = 64L * 1024 * 1024, IPreviewProvider? provider = null)
    { if (maximumBytes < 4) throw new ArgumentOutOfRangeException(nameof(maximumBytes)); MaximumBytes = maximumBytes; _provider = provider ?? new OpenCvPreviewProvider(); }
    private sealed record PreviewKey(FrameHandle Buffer, int Width, int Height, int Stride, PixelFormat Format, double X, double Y);
    public async Task<ImagePreview> GetAsync(ImageFrame source, CancellationToken ct)
    {
        var key = new PreviewKey(source.BufferHandle, source.Width, source.Height, source.Stride, source.PixelFormat, source.CoordinateSpace.OriginX, source.CoordinateSpace.OriginY);
        long epoch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            epoch = _epoch;
            if (_entries.TryGetValue(key, out var found)) { _lru.Remove(found); _lru.AddFirst(found); return found.Value.Preview; }
        }
        var preview = await _provider.CreateAsync(source, 2048, Math.Min(MaximumBytes, 16L * 1024 * 1024), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (epoch != _epoch) return preview;
            if (_entries.TryGetValue(key, out var found)) return found.Value.Preview;
            if (preview.Bytes > MaximumBytes) throw new InvalidOperationException("Preview exceeds cache budget.");
            while (_bytes + preview.Bytes > MaximumBytes && _lru.Last is { } last)
            { _entries.Remove(last.Value.Key); _bytes -= last.Value.Preview.Bytes; _lru.RemoveLast(); }
            var node = _lru.AddFirst((key, preview)); _entries.Add(key, node); _bytes += preview.Bytes;
            return preview;
        }
    }
    public void Clear() { lock (_gate) { _epoch++; _entries.Clear(); _lru.Clear(); _bytes = 0; } }
    public void Dispose() { lock (_gate) { _disposed = true; _epoch++; _entries.Clear(); _lru.Clear(); _bytes = 0; } }
}

using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Imaging;

public static class FrameFactory
{
    /// <summary>Copies caller memory once; no caller buffer is retained. Stride includes source row padding.</summary>
    public static ImageFrame CopyFrom(ReadOnlySpan<byte> data, int width, int height, PixelFormat format,
        int? stride = null, ImageCoordinateSpace? coordinateSpace = null)
    {
        var rowBytes = checked(width * format.BytesPerPixel());
        var sourceStride = stride ?? rowBytes;
        if (width <= 0 || height <= 0 || sourceStride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride));
        var required = checked((long)(height - 1) * sourceStride + rowBytes);
        if (data.Length < required) throw new ArgumentException("Pixel buffer is shorter than the declared image and stride.", nameof(data));
        using var builder = new ImageFrameBuilder(width, height, format, sourceStride, coordinateSpace);
        for (var y = 0; y < height; y++) data.Slice(checked(y * sourceStride), rowBytes).CopyTo(builder.GetRowSpan(y));
        return builder.Build();
    }

    /// <summary>Copies a Mat into platform-owned immutable memory. Format is explicit, so RGB is never guessed.</summary>
    public static unsafe ImageFrame CopyFromMat(Mat mat, PixelFormat format, ImageCoordinateSpace? coordinateSpace = null)
    {
        ArgumentNullException.ThrowIfNull(mat);
        if (mat.Empty() || mat.Depth() != MatType.CV_8U || mat.Channels() != format.BytesPerPixel())
            throw new ArgumentException("Mat must contain nonempty 8-bit pixels matching the declared format.", nameof(mat));
        var width = mat.Cols; var height = mat.Rows; var rowBytes = checked(width * mat.Channels());
        using var builder = new ImageFrameBuilder(width, height, format, coordinateSpace: coordinateSpace);
        for (var y = 0; y < height; y++) new ReadOnlySpan<byte>((void*)mat.Ptr(y), rowBytes).CopyTo(builder.GetRowSpan(y));
        return builder.Build();
    }
}

/// <summary>Read-only by contract Mat header over a leased frame, with no pixel copy. Do not write to Mat.</summary>
public sealed class OpenCvImageLease : IDisposable
{
    private BufferLease? _lease;
    public Mat Mat { get; }
    public OpenCvImageLease(ImageFrame image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _lease = image.Lease();
        try { Mat = Mat.FromPixelData(image.Height, image.Width, image.PixelFormat.BytesPerPixel() == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3, _lease.DataPointer, image.Stride); }
        catch { _lease.Dispose(); _lease = null; throw; }
    }
    public void Dispose() { Mat.Dispose(); Interlocked.Exchange(ref _lease, null)?.Dispose(); }
}

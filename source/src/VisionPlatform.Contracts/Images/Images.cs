using System.Runtime.InteropServices;
using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Contracts.Images;

public enum PixelFormat { Mono8, BayerRG8, BayerGB8, RGB8, BGR8 }

public static class PixelFormatExtensions
{
    public static int BytesPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.Mono8 or PixelFormat.BayerRG8 or PixelFormat.BayerGB8 => 1,
        PixelFormat.RGB8 or PixelFormat.BGR8 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported pixel format.")
    };

    public static bool IsBayer(this PixelFormat format) => format is PixelFormat.BayerRG8 or PixelFormat.BayerGB8;
}

/// <summary>Reference counted native allocation. Frames and leases release only their own reference.</summary>
public sealed class FrameHandle
{
    private nint _pointer;
    private int _references = 1;
    private static long _activeBytes;
    private static long _activeAllocations;
    /// <summary>Identity of this pixel allocation, shared by views without extending their lifetime.</summary>
    public Guid BufferId { get; } = Guid.NewGuid();
    public long Length { get; }
    public static long ActiveBytes => Interlocked.Read(ref _activeBytes);
    public static long ActiveAllocations => Interlocked.Read(ref _activeAllocations);
    public int ReferenceCount => Math.Max(0, Volatile.Read(ref _references));

    internal FrameHandle(long length)
    {
        if (length <= 0 || length > nint.MaxValue) throw new ArgumentOutOfRangeException(nameof(length));
        Length = length;
        _pointer = Marshal.AllocHGlobal(checked((nint)length));
        Interlocked.Add(ref _activeBytes, length);
        Interlocked.Increment(ref _activeAllocations);
    }

    internal nint Pointer => Volatile.Read(ref _pointer) is var p && p != 0 ? p : throw new ObjectDisposedException(nameof(FrameHandle));

    internal void AddReference()
    {
        var current = Volatile.Read(ref _references);
        while (current > 0)
        {
            var previous = Interlocked.CompareExchange(ref _references, checked(current + 1), current);
            if (previous == current) return;
            current = previous;
        }
        throw new ObjectDisposedException(nameof(FrameHandle));
    }

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            Free();
            GC.SuppressFinalize(this);
        }
    }

    private void Free()
    {
        var p = Interlocked.Exchange(ref _pointer, 0);
        if (p == 0) return;
        Marshal.FreeHGlobal(p);
        Interlocked.Add(ref _activeBytes, -Length);
        Interlocked.Decrement(ref _activeAllocations);
    }

    ~FrameHandle() => Free();
}

/// <summary>Published images are read only. Share and ROI view never copy pixel data.</summary>
public class ImageFrame : IDisposable
{
    private readonly object _gate = new();
    private FrameHandle? _handle;
    internal long Offset { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat PixelFormat { get; }
    public Guid FrameId { get; }
    public DateTimeOffset Timestamp { get; }
    public ImageCoordinateSpace CoordinateSpace { get; }
    // Original sensor pattern remains the format; phase changes when cropping a Bayer frame.
    public int MosaicOffsetX { get; }
    public int MosaicOffsetY { get; }
    public bool IsDisposed { get { lock (_gate) return _handle is null; } }
    public FrameHandle BufferHandle { get { lock (_gate) return _handle ?? throw new ObjectDisposedException(nameof(ImageFrame)); } }

    internal ImageFrame(FrameHandle handle, int width, int height, int stride, PixelFormat format,
        long offset, Guid frameId, DateTimeOffset timestamp, ImageCoordinateSpace coordinateSpace,
        int mosaicOffsetX = 0, int mosaicOffsetY = 0)
    {
        Width = width; Height = height; Stride = stride; PixelFormat = format;
        Offset = offset; FrameId = frameId; Timestamp = timestamp; CoordinateSpace = coordinateSpace;
        MosaicOffsetX = mosaicOffsetX; MosaicOffsetY = mosaicOffsetY; _handle = handle;
    }

    public BufferLease Lease()
    {
        lock (_gate)
        {
            var h = _handle ?? throw new ObjectDisposedException(nameof(ImageFrame));
            h.AddReference();
            return new BufferLease(h, Offset, Width, Height, Stride, PixelFormat.BytesPerPixel());
        }
    }

    public ImageFrame Share()
    {
        lock (_gate)
        {
            var h = _handle ?? throw new ObjectDisposedException(nameof(ImageFrame));
            h.AddReference();
            return new ImageFrame(h, Width, Height, Stride, PixelFormat, Offset, FrameId, Timestamp,
                CoordinateSpace, MosaicOffsetX, MosaicOffsetY);
        }
    }

    public ImageView CreateView(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > Width || (long)y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width), "ROI must be nonempty and entirely inside the image.");
        lock (_gate)
        {
            var h = _handle ?? throw new ObjectDisposedException(nameof(ImageFrame));
            h.AddReference();
            return new ImageView(h, width, height, Stride, PixelFormat,
                checked(Offset + (long)y * Stride + (long)x * PixelFormat.BytesPerPixel()), FrameId,
                Timestamp, CoordinateSpace with { OriginX = CoordinateSpace.OriginX + x, OriginY = CoordinateSpace.OriginY + y },
                (MosaicOffsetX + x) & 1, (MosaicOffsetY + y) & 1);
        }
    }

    public void Dispose()
    {
        FrameHandle? h;
        lock (_gate) { h = _handle; _handle = null; }
        h?.ReleaseReference();
        GC.SuppressFinalize(this);
    }

    ~ImageFrame() => Dispose();
}

public sealed class ImageView : ImageFrame
{
    internal ImageView(FrameHandle handle, int width, int height, int stride, PixelFormat format,
        long offset, Guid frameId, DateTimeOffset timestamp, ImageCoordinateSpace space, int phaseX, int phaseY)
        : base(handle, width, height, stride, format, offset, frameId, timestamp, space, phaseX, phaseY) { }
}

public sealed class BufferLease : IDisposable
{
    private FrameHandle? _handle;
    private readonly long _offset;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int RowBytes { get; }
    public nint DataPointer => checked((_handle ?? throw new ObjectDisposedException(nameof(BufferLease))).Pointer + (nint)_offset);

    internal BufferLease(FrameHandle handle, long offset, int width, int height, int stride, int bytesPerPixel)
    { _handle = handle; _offset = offset; Width = width; Height = height; Stride = stride; RowBytes = checked(width * bytesPerPixel); }

    public unsafe ReadOnlySpan<byte> GetRowSpan(int row)
    {
        if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
        return new ReadOnlySpan<byte>((void*)(DataPointer + checked((nint)((long)row * Stride))), RowBytes);
    }

    public void Dispose() { Interlocked.Exchange(ref _handle, null)?.ReleaseReference(); GC.SuppressFinalize(this); }
    ~BufferLease() => Dispose();
}

/// <summary>Exclusive mutable construction. Build transfers ownership and invalidates this writer.</summary>
public sealed class ImageFrameBuilder : IDisposable
{
    private FrameHandle? _handle;
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat PixelFormat { get; }
    public ImageCoordinateSpace CoordinateSpace { get; }
    public nint DataPointer => (_handle ?? throw new ObjectDisposedException(nameof(ImageFrameBuilder))).Pointer;

    public unsafe ImageFrameBuilder(int width, int height, PixelFormat format, int? stride = null, ImageCoordinateSpace? coordinateSpace = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var rowBytes = checked(width * format.BytesPerPixel());
        var actualStride = stride ?? rowBytes;
        if (actualStride < rowBytes) throw new ArgumentOutOfRangeException(nameof(stride), "Stride is smaller than a pixel row.");
        var length = checked((long)actualStride * height);
        Width = width; Height = height; Stride = actualStride; PixelFormat = format;
        CoordinateSpace = coordinateSpace ?? new ImageCoordinateSpace();
        _handle = new FrameHandle(length);
        // Clear padding too, so no uninitialized native bytes are exposed to file writers.
        for (var row = 0; row < height; row++) new Span<byte>((void*)(DataPointer + checked((nint)((long)row * Stride))), Stride).Clear();
    }

    public unsafe Span<byte> GetRowSpan(int row)
    {
        if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
        return new Span<byte>((void*)(DataPointer + checked((nint)((long)row * Stride))), checked(Width * PixelFormat.BytesPerPixel()));
    }

    public ImageFrame Build(Guid? frameId = null, DateTimeOffset? timestamp = null)
    {
        var h = Interlocked.Exchange(ref _handle, null) ?? throw new ObjectDisposedException(nameof(ImageFrameBuilder));
        return new ImageFrame(h, Width, Height, Stride, PixelFormat, 0, frameId ?? Guid.NewGuid(), timestamp ?? DateTimeOffset.UtcNow, CoordinateSpace);
    }

    public void Dispose() { Interlocked.Exchange(ref _handle, null)?.ReleaseReference(); GC.SuppressFinalize(this); }
    ~ImageFrameBuilder() => Dispose();
}

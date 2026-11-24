namespace VisionPlatform.Contracts.Images;

public enum WorkingColorSpace { Hsv8, Lab8 }
/// <summary>Typed working channels, NOT RGB/BGR pixels. Uses original frame leases/allocation internally.</summary>
public sealed class ColorPlaneFrame : IDisposable
{
    private readonly ImageFrame _storage;
    public WorkingColorSpace ColorSpace { get; }
    public int Width => _storage.Width;
    public int Height => _storage.Height;
    public int Stride => _storage.Stride;
    public Guid FrameId => _storage.FrameId;
    public DateTimeOffset Timestamp => _storage.Timestamp;
    public override string ToString() => $"{ColorSpace} {Width}×{Height}, stride={Stride}; 工作通道，预览请查看Preview输出";
    public Geometry.ImageCoordinateSpace CoordinateSpace => _storage.CoordinateSpace;
    public long StorageBytes => _storage.BufferHandle.Length;
    public FrameHandle BufferHandle => _storage.BufferHandle;
    public ColorPlaneFrame(ImageFrame storage, WorkingColorSpace colorSpace)
    {
        if (!Enum.IsDefined(colorSpace) || storage.PixelFormat.BytesPerPixel() != 3 || storage.PixelFormat.IsBayer()) throw new ArgumentException("Working channels require three 8-bit samples.");
        _storage = storage.Share(); ColorSpace = colorSpace;
    }
    public BufferLease Lease() => _storage.Lease();
    public void Dispose() => _storage.Dispose();
}

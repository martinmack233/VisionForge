using VisionPlatform.Contracts.Geometry;
using System.Runtime.InteropServices;

namespace VisionPlatform.Contracts.Images;
/// <summary>Signed Float32 numerical raster, NOT Mono8. Uses the existing native allocation/lease mechanism.</summary>
public sealed class FloatPlaneFrame:IDisposable
{
    private readonly object _gate=new();private FrameHandle? _handle;
    public int Width{get;}public int Height{get;}public int Stride=>checked(Width*4);
    public Guid FrameId{get;}public DateTimeOffset Timestamp{get;}public ImageCoordinateSpace CoordinateSpace{get;}
    public string Quantity{get;}public string Unit{get;}
    public FrameHandle BufferHandle{get{lock(_gate)return _handle??throw new ObjectDisposedException(nameof(FloatPlaneFrame));}}
    internal FloatPlaneFrame(FrameHandle handle,int width,int height,Guid frameId,DateTimeOffset timestamp,ImageCoordinateSpace space,string quantity,string unit)
    {_handle=handle;Width=width;Height=height;FrameId=frameId;Timestamp=timestamp;CoordinateSpace=space;Quantity=quantity;Unit=unit;}
    public BufferLease Lease(){lock(_gate){var h=_handle??throw new ObjectDisposedException(nameof(FloatPlaneFrame));h.AddReference();return new(h,0,Width,Height,Stride,4);}}
    public FloatPlaneFrame Share(){lock(_gate){var h=_handle??throw new ObjectDisposedException(nameof(FloatPlaneFrame));h.AddReference();return new(h,Width,Height,FrameId,Timestamp,CoordinateSpace,Quantity,Unit);}}
    public void Dispose(){FrameHandle? h;lock(_gate){h=_handle;_handle=null;}h?.ReleaseReference();GC.SuppressFinalize(this);}
    ~FloatPlaneFrame()=>Dispose();
    public override string ToString()=>$"Float32 {Width}×{Height} {Quantity} [{Unit}]" +
        (Quantity == "neighborhood-sum" ? "" : "；查看Preview端口，预览不是原始数值");
}
public sealed class FloatPlaneBuilder:IDisposable
{
    public const long MaximumPixels=16_000_000;private FrameHandle? _handle;
    public int Width{get;}public int Height{get;}public int Stride=>checked(Width*4);
    public nint DataPointer=>(_handle??throw new ObjectDisposedException(nameof(FloatPlaneBuilder))).Pointer;
    public FloatPlaneBuilder(int width,int height)
    {if(width<1||height<1||(long)width*height>MaximumPixels)throw new ArgumentException("浮点图像最多1600万像素，请显式使用ROI。");Width=width;Height=height;_handle=new(checked((long)width*height*4));for(var y=0;y<Height;y++)GetRowSpan(y).Clear();}
    public unsafe Span<float> GetRowSpan(int row){if((uint)row>=Height)throw new ArgumentOutOfRangeException(nameof(row));return new((void*)(DataPointer+(nint)((long)row*Stride)),Width);}
    public FloatPlaneFrame Build(Guid frameId,DateTimeOffset timestamp,ImageCoordinateSpace space,string quantity="derivative",string unit="intensity/pixel")
    {var h=Interlocked.Exchange(ref _handle,null)??throw new ObjectDisposedException(nameof(FloatPlaneBuilder));return new(h,Width,Height,frameId,timestamp,space,quantity,unit);}
    public void Dispose(){Interlocked.Exchange(ref _handle,null)?.ReleaseReference();GC.SuppressFinalize(this);}
    ~FloatPlaneBuilder()=>Dispose();
}

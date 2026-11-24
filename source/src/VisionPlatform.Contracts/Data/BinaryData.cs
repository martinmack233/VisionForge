namespace VisionPlatform.Contracts.Data;
/// <summary>Immutable bounded payload. Constructing and ToArray make explicit copies; never contains native handles.</summary>
public sealed class BinaryData
{
    public const int MaximumLength=16*1024*1024;private readonly byte[] _bytes;
    public int Count=>_bytes.Length;public byte this[int index]=>_bytes[index];
    public BinaryData(IEnumerable<byte> bytes){_bytes=bytes.Take(MaximumLength+1).ToArray();if(_bytes.Length>MaximumLength)throw new ArgumentException("二进制数据超过16MiB预算。");}
    public byte[] ToArray()=>(byte[])_bytes.Clone();
    public override string ToString()=>$"二进制数据 {Count} bytes（只读）";
}

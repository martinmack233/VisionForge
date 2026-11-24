using System.Text;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.AI;

/// <summary>Fixed uncompressed 8-bit TIFF, packed rows. Original frame remains borrowed/read-only.</summary>
public static class ControlledTiffChannel
{
    public static void Write(ImageFrame frame, string path, CancellationToken ct)
    {
        if (frame.PixelFormat is not (PixelFormat.Mono8 or PixelFormat.RGB8 or PixelFormat.BGR8)) throw new ArgumentException("TIFF channel requires explicit Mono8/RGB8/BGR8.");
        var channels = frame.PixelFormat.BytesPerPixel(); var rowBytes = checked(frame.Width * channels); var length = checked((long)rowBytes * frame.Height);
        if (length > 300_000_000) throw new ArgumentException("Image channel exceeds 300 MB.");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write((uint)8); writer.Write((ushort)11);
        var tags = new (ushort Tag, ushort Type, uint Count, uint Value)[] {
            (256,4,1,(uint)frame.Width),(257,4,1,(uint)frame.Height),(258,3,(uint)channels,channels==1?8u:148u),
            (259,3,1,1),(262,3,1,channels==1?1u:2u),(273,4,1,160),(274,3,1,1),(277,3,1,(uint)channels),
            (278,4,1,(uint)frame.Height),(279,4,1,(uint)length),(284,3,1,1) };
        foreach (var t in tags) { writer.Write(t.Tag); writer.Write(t.Type); writer.Write(t.Count); writer.Write(t.Value); }
        writer.Write((uint)0); while (stream.Position < 148) writer.Write((byte)0);
        writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8); while (stream.Position < 160) writer.Write((byte)0);
        using var lease = frame.Lease(); var row = new byte[rowBytes];
        for (var y = 0; y < frame.Height; y++)
        {
            ct.ThrowIfCancellationRequested(); lease.GetRowSpan(y).CopyTo(row);
            if (frame.PixelFormat == PixelFormat.BGR8) for (var x = 0; x < rowBytes; x += 3) (row[x], row[x+2]) = (row[x+2], row[x]);
            stream.Write(row);
        }
        stream.Flush();
    }
}

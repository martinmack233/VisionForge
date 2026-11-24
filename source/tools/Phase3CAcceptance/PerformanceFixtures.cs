using System.Diagnostics;
using System.Security.Cryptography;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;

internal static class PerformanceFixtures
{
    public static byte MonoPixel(int x, int y) => ((x ^ y) & 1) == 0 ? (byte)23 : (byte)231;

    public static ImageFrame Create(int width, int height, PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(width, height, format);
        for (var y = 0; y < height; y++)
        {
            var row = builder.GetRowSpan(y);
            for (var x = 0; x < width; x++)
            {
                var r = (byte)(x / 128 % 2 == 0 ? 220 : 40);
                var g = (byte)(y / 128 % 2 == 0 ? 100 : 160);
                var b = (byte)(x / 128 % 2 == 0 ? 30 : 230);
                if (format == PixelFormat.Mono8) row[x] = MonoPixel(x, y);
                else if (format == PixelFormat.RGB8) { row[x * 3] = r; row[x * 3 + 1] = g; row[x * 3 + 2] = b; }
                else if (format == PixelFormat.BGR8) { row[x * 3] = b; row[x * 3 + 1] = g; row[x * 3 + 2] = r; }
                else if (format == PixelFormat.BayerRG8) row[x] = (y % 2, x % 2) switch { (0, 0) => r, (1, 1) => b, _ => g };
                else row[x] = (y % 2, x % 2) switch { (0, 1) => b, (1, 0) => r, _ => g };
            }
        }
        return builder.Build();
    }

    public static string Hash(ImageFrame frame)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var lease = frame.Lease();
        for (var y = 0; y < frame.Height; y++) hash.AppendData(lease.GetRowSpan(y));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static object Memory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64,
            PlatformImageBytes = FrameHandle.ActiveBytes, PlatformAllocations = FrameHandle.ActiveAllocations,
            ManagedBytes = GC.GetTotalMemory(false) };
    }
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
}

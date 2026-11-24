using System.Reflection;
using System.Runtime.InteropServices;

namespace VisionPlatform.Contracts.Build;
public static class ProductBuild
{
    private static readonly Assembly Assembly = typeof(ProductBuild).Assembly;
    public static string Name => Metadata("VisionProductName");
    public static string Version => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string Commit => Metadata("VisionSourceCommit");
    public static string SourceState => Metadata("VisionSourceState");
    public static string SourceHash => Metadata("VisionSourceHash");
    public static string Runtime => RuntimeInformation.FrameworkDescription;
    public static string Identity => $"{Commit} · {SourceState}\n源码清单 SHA256：{SourceHash}";
    private static string Metadata(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? "unknown";
}

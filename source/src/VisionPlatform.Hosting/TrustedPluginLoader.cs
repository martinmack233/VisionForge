using System.Runtime.Loader;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Hosting;

/// <summary>Explicit startup-only trusted code loading. This is not a security sandbox or hot reload.</summary>
public static class TrustedPluginLoader
{
    public static int Load(NodeRegistry registry, string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Plugin paths must be explicit absolute paths.");
        var path = Path.GetFullPath(absolutePath);
        if (!File.Exists(path) || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("A trusted managed plugin DLL is required.", path);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return registry.Discover(assembly);
    }
}

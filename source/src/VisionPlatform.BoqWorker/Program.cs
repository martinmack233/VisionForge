using VisionPlatform.Acquisition.Boq;

if (args.Contains("--version")) { Console.WriteLine($"{VisionPlatform.Contracts.Build.ProductBuild.Name} {VisionPlatform.Contracts.Build.ProductBuild.Version}\n{VisionPlatform.Contracts.Build.ProductBuild.Runtime}\n{VisionPlatform.Contracts.Build.ProductBuild.Identity}"); return 0; }
if (args.Length == 2 && args[0] == "--inspect-sdk")
{
    try
    {
        var path = Path.GetFullPath(args[1]);
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        var type = assembly.GetType("BoqApi_CSharp.BoqApi", throwOnError: true)!;
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Loaded = true, Runtime = VisionPlatform.Contracts.Build.ProductBuild.Runtime, Assembly = assembly.FullName, PublicMethods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Select(m => m.Name).Distinct().ToArray(), CameraInitialized = false, DeviceDiscoveryCalled = false }));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Loaded = false, Error = ex.GetBaseException().Message, CameraInitialized = false })); return 1; }
}
return await BoqWorkerEntryPoint.RunAsync(args);

using Microsoft.Extensions.Logging;
using System.Text.Json;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Hosting;
using VisionPlatform.Plugins.Builtin;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try { return await MainAsync(args, shutdown.Token); }
catch (OperationCanceledException) { Console.Error.WriteLine("RuntimeHost cancelled; execution scope has been released."); return 130; }
catch (Exception ex) { Console.Error.WriteLine("RuntimeHost error: " + ex.Message); return 1; }

static async Task<int> MainAsync(string[] args, CancellationToken ct)
{
    var machineIndex = Array.IndexOf(args, "--machine-config");
    if (machineIndex >= 0) Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG", Path.GetFullPath(args.ElementAtOrDefault(machineIndex + 1) ?? throw new ArgumentException("Missing machine config path.")));
    if (args.Contains("--version")) { Console.WriteLine($"{VisionPlatform.Contracts.Build.ProductBuild.Name} {VisionPlatform.Contracts.Build.ProductBuild.Version}\n{VisionPlatform.Contracts.Build.ProductBuild.Runtime}\n{VisionPlatform.Contracts.Build.ProductBuild.Identity}"); return 0; }
    if (args.Contains("--help"))
    {
        Console.WriteLine($"{VisionPlatform.Contracts.Build.ProductBuild.Name} RuntimeHost {VisionPlatform.Contracts.Build.ProductBuild.Version} ({VisionPlatform.Contracts.Build.ProductBuild.Runtime} / Windows x64)\n" +
            "  --address 127.0.0.1 --port 8766 [--allow-lan] [--plugin absolute.dll]...\n" +
            "  --project project.json --once [--target nodeId] [--timeout 60000] [--plugin absolute.dll]...\n" +
            "Server projects are loaded only by explicit client load commands. No project executes on startup.\n" +
            "Wire v1: 4-byte big-endian UTF8 JSON length prefix. Use tools/runtimehost_client.py."); return 0;
    }
    var allowed = new HashSet<string> { "--address", "--port", "--allow-lan", "--plugin", "--project", "--once", "--target", "--timeout", "--machine-config" };
    for (var i = 0; i < args.Length; i++)
    {
        if (!allowed.Contains(args[i])) throw new ArgumentException("Unknown argument: " + args[i]);
        if (args[i] is not ("--allow-lan" or "--once") && ++i >= args.Length) throw new ArgumentException("An option value is missing.");
    }
    string? Value(string key) { var index = Array.IndexOf(args, key); return index < 0 ? null : args[index + 1]; }
    var registry = BuiltinRegistration.CreateRegistry();
    for (var i = 0; i < args.Length - 1; i++) if (args[i] == "--plugin")
    { var count = TrustedPluginLoader.Load(registry, args[++i]); Console.Error.WriteLine($"Loaded {count} trusted plugin node(s)."); }
    var matcher = new CvInspectTemplateMatcher(); var versions = new Dictionary<string, string> { [matcher.AlgorithmId] = matcher.Version };
    using var logger = new HostConsoleLogger();
    if (args.Contains("--once"))
    {
        var project = Value("--project") ?? throw new ArgumentException("--once requires --project.");
        await using var session = new RuntimeHostSession(registry, versions, logger);
        var loaded = await session.HandleAsync(Request("load", new LoadProjectCommand(Path.GetFullPath(project))), ct);
        if (!loaded.Success) { Console.WriteLine(JsonSerializer.Serialize(loaded, JsonDefaults.Options)); return 2; }
        var run = await session.HandleAsync(Request("run", new RunProjectCommand(Value("--target"), int.Parse(Value("--timeout") ?? "60000", System.Globalization.CultureInfo.InvariantCulture))), ct);
        if (!run.Success) { Console.WriteLine(JsonSerializer.Serialize(run, JsonDefaults.Options)); return 2; }
        var id = run.Payload!.Value.Deserialize<HostRunAccepted>(JsonDefaults.Options)!.RunId;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); var status = await session.HandleAsync(Request("status", new RunReference(id)), ct);
            var state = status.Payload!.Value.Deserialize<HostStatusSnapshot>(JsonDefaults.Options)!;
            if (state.State != HostSessionState.Running) break;
            await Task.Delay(20, ct);
        }
        var result = await session.HandleAsync(Request("result", new RunReference(id)), ct);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonDefaults.Options));
        return result.Success && result.Payload!.Value.Deserialize<HostResultSnapshot>(JsonDefaults.Options)!.Success ? 0 : 2;
    }
    if (Value("--project") is not null || Value("--target") is not null) throw new ArgumentException("--project/--target require --once; server clients explicitly load their own isolated sessions.");
    await using var server = new RuntimeHostServer(registry, new RuntimeHostOptions
    { ListenAddress = Value("--address") ?? "127.0.0.1", Port = int.Parse(Value("--port") ?? "8766", System.Globalization.CultureInfo.InvariantCulture), AllowLan = args.Contains("--allow-lan") }, versions, logger);
    await server.StartAsync(ct);
    Console.WriteLine(JsonSerializer.Serialize(new { ProtocolVersion = 1, Event = "Listening", Address = Value("--address") ?? "127.0.0.1", Port = server.BoundPort, Nodes = registry.RegisteredTypeIds.Count }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false }));
    try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    await server.StopAsync(); return 0;
}
static HostRequest Request(string operation, object payload) => new() { RequestId = Guid.NewGuid().ToString("N"), Operation = operation, Payload = JsonDefaults.Element(payload) };

internal sealed class HostConsoleLogger : ILogger, IDisposable
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { if (IsEnabled(level)) Console.Error.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + level + " " + formatter(state, null)); }
    public void Dispose() { }
}

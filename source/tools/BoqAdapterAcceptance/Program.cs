using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.Acquisition.Boq;
using VisionPlatform.Contracts.Acquisition;

if (args.Length < 1) { Console.Error.WriteLine("BoqAdapterAcceptance <SDK DLL path> [--worker <exe>] [--simulator-capture <serial>]"); return 2; }
string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
var provider = Option("--worker") is { } worker ? new BoqCameraProvider(args[0], worker) : new BoqCameraProvider(args[0]);
var watch = Stopwatch.StartNew();
var devices = await provider.EnumerateAsync();
var enumerationMs = watch.Elapsed.TotalMilliseconds;
var captures = new List<object>();
var lifecycle = new List<string>();
var serial = Option("--simulator-capture");
try
{
    if (serial is not null)
    {
        var identity = devices.Single(d => d.DeviceId == serial);
        if (!identity.IsVirtual) throw new InvalidOperationException("此验收工具仅允许SDK明确标识的模拟设备，禁止连接候选实体相机。");
        await using var device = provider.Create(serial);
        await device.OpenAsync(new()); lifecycle.Add("Open:" + device.State);
        var capabilities = device.Capabilities;
        using (var frame = await device.CaptureSingleAsync())
            captures.Add(new { Mode = "single", device.LastFrame, Capabilities = capabilities, Allocation = frame.BufferHandle.Length });
        await device.StartAcquisitionAsync(); lifecycle.Add("Start:" + device.State);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var count = 0;
        await foreach (var frame in device.ReadFramesAsync(timeout.Token))
        {
            using (frame) captures.Add(new { Mode = "continuous", device.LastFrame, Allocation = frame.BufferHandle.Length });
            if (++count == 3) break;
        }
        await device.StopAcquisitionAsync(); lifecycle.Add("Stop:" + device.State);
        await device.CloseAsync(); lifecycle.Add("Close:" + device.State);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Success = provider.LastError is null, SdkPath = Path.GetFullPath(args[0]), enumerationMs,
        Devices = devices, provider.LastError, Lifecycle = lifecycle, Captures = captures,
        RealHardwareCaptureVerified = false, PermanentConfigurationChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
    return provider.LastError is null ? 0 : 1;
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { Success = false, Error = ex.ToString(), Lifecycle = lifecycle, Captures = captures,
        Devices = devices, RealHardwareCaptureVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
    return 1;
}

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>Runs only inside the disposable vendor worker. Two named pipes separate commands and raw pixel bytes.</summary>
public static class BoqWorkerEntryPoint
{
    internal const int FrameMagic = 0x42514631;
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != "--control" || args[2] != "--frames" || args[4] != "--sdk") return 2;
        using var control = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var frames = new NamedPipeClientStream(".", args[3], PipeDirection.Out, PipeOptions.Asynchronous);
        using var lifetime = new CancellationTokenSource();
        await Task.WhenAll(control.ConnectAsync(8000), frames.ConnectAsync(8000)).ConfigureAwait(false);
        using var reader = new StreamReader(control, Encoding.UTF8, false, 4096, true);
        using var writer = new StreamWriter(control, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        var queue = Channel.CreateBounded<BoqSdkFrame>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var pump = Task.Run(async () =>
        {
            await foreach (var frame in queue.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var header = new byte[28];
                BitConverter.TryWriteBytes(header.AsSpan(0), FrameMagic); BitConverter.TryWriteBytes(header.AsSpan(4), frame.Width);
                BitConverter.TryWriteBytes(header.AsSpan(8), frame.Height); BitConverter.TryWriteBytes(header.AsSpan(12), frame.ImageSize);
                BitConverter.TryWriteBytes(header.AsSpan(16), frame.PixelFormat); BitConverter.TryWriteBytes(header.AsSpan(20), frame.ReceivedAt.UtcTicks);
                await frames.WriteAsync(header, lifetime.Token).ConfigureAwait(false);
                if (frame.ImageSize > 0) await frames.WriteAsync(frame.Buffer.AsMemory(0, frame.ImageSize), lifetime.Token).ConfigureAwait(false);
                await frames.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        });
        var factory = new ReflectionBoqSdkFactory(args[5]);
        IBoqSdkCamera? camera = null;
        void OnFrame(BoqSdkFrame source)
        {
            // Copy while the vendor callback owns the buffer. No JSON/base64 or SDK object crosses IPC.
            if (source.ImageSize <= 0 || source.ImageSize > BoqCameraDevice.MaximumFrameBytes || source.Buffer.Length < source.ImageSize) return;
            var owned = source.Buffer.AsSpan(0, source.ImageSize).ToArray();
            queue.Writer.TryWrite(source with { Buffer = owned });
        }
        void OnDisconnected() => queue.Writer.TryWrite(new([], 0, 0, 0, 0, DateTimeOffset.UtcNow));
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 64 * 1024) break;
                BoqWorkerResponse response;
                var close = false;
                try
                {
                    var request = JsonSerializer.Deserialize<BoqWorkerCommand>(line) ?? throw new InvalidDataException("缺少控制命令。");
                    object? result = null;
                    switch (request.Operation)
                    {
                        case "Enumerate": result = factory.Enumerate(); break;
                        case "Connect":
                            if (camera is not null) throw new InvalidOperationException("设备已经打开。");
                            camera = factory.Connect(request.Value!.Value.GetString()!);
                            camera.FrameReceived += OnFrame; camera.Disconnected += OnDisconnected;
                            break;
                        case "Capabilities": result = Required().QueryCapabilities(); break;
                        case "ReadParameters": result = Required().ReadParameters(); break;
                        case "SetParameter": Required().SetParameter(request.Value!.Value.GetProperty("Name").GetString()!, request.Value.Value.GetProperty("Value")); break;
                        case "GrabOne": Required().GrabOne(request.Value!.Value.GetUInt32()); break;
                        case "Start": Required().Start(); break;
                        case "Stop": Required().Stop(); break;
                        case "Close": camera?.Dispose(); camera = null; close = true; break;
                        default: throw new InvalidDataException("未知博峤命令。");
                    }
                    response = new(true, JsonSerializer.SerializeToElement(result));
                }
                catch (Exception ex) { response = new(false, Error: ex.GetBaseException().Message); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                if (close) return 0;
            }
            return 0;
        }
        finally
        {
            lifetime.Cancel(); queue.Writer.TryComplete();
            if (camera is not null) { camera.FrameReceived -= OnFrame; camera.Disconnected -= OnDisconnected; camera.Dispose(); }
            try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
        }
        IBoqSdkCamera Required() => camera ?? throw new InvalidOperationException("请先连接博峤设备。");
    }
}

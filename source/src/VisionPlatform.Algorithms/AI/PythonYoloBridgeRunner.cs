using System.Diagnostics;
using System.Text;

namespace VisionPlatform.Algorithms.AI;

/// <summary>One inference process per request: bounded output, no shell, no inherited model/previous-run data.</summary>
public sealed class PythonYoloBridgeRunner : IYoloBridgeRunner
{
    public const int MaximumOutputCharacters = 8 * 1024 * 1024;
    public async Task<string> RunAsync(string pythonExecutable, string bridgeScript, string requestPath,
        int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(pythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!
        };
        info.ArgumentList.Add("-B"); info.ArgumentList.Add("-u"); info.ArgumentList.Add(bridgeScript); info.ArgumentList.Add(requestPath);
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["YOLO_CONFIG_DIR"] = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
        using var process = new Process { StartInfo = info };
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("The external Python process could not start.");
        WindowsInferenceJob? ownedJob;
        try { ownedJob = WindowsInferenceJob.Attach(process); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        using var job = ownedJob;
        var stdout = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, linked.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 65536, linked.Token);
        var exit = process.WaitForExitAsync(linked.Token);
        try
        {
            // Output floods must stop the child immediately, not wait for its eventual exit.
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(linked.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            if (process.ExitCode != 0)
            {
                var response = (await stdout.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException($"External YOLO process failed (exit {process.ExitCode}). " +
                    "Review the configured environment/model and the bounded bridge error response: " + response[..Math.Min(response.Length, 2048)]);
            }
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException($"YOLO inference exceeded {timeoutMilliseconds} ms; the external process was terminated."); }
        finally
        {
            // Kill the kernel job before waiting: venv launchers can exit while grandchildren still live.
            job?.Terminate();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // Always observe asynchronous reads; no background work survives request directory cleanup.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return result.ToString();
            if (result.Length + read > maximum) throw new InvalidDataException("External YOLO process exceeded its output/log size budget.");
            result.Append(buffer, 0, read);
        }
    }
}

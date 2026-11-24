using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VisionPlatform.Algorithms.AI;

public static class DetectionComponentTool
{
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "tools", "ai_runtime_component", "component.py");
    public static string RecipePath => Path.Combine(AppContext.BaseDirectory, "components", "ai-runtime", "recipe.json");
    public static async Task<JsonElement> RunAsync(string python, IEnumerable<string> arguments, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python)) throw new FileNotFoundException("请选择存在的Python绝对路径，不会自动安装环境。");
        if (!File.Exists(ScriptPath)) throw new FileNotFoundException("检测组件制作/诊断工具缺失，请修复程序安装。");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add(ScriptPath);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var process = Process.Start(start) ?? throw new IOException("无法启动组件工具。");
        try
        {
            var stdout = BoundedAsync(process.StandardOutput, linked.Token); var stderr = BoundedAsync(process.StandardError, linked.Token);
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), stdout, stderr).ConfigureAwait(false);
            using var response = JsonDocument.Parse(await stdout.ConfigureAwait(false)); var result = response.RootElement.Clone();
            if (process.ExitCode != 0 || !result.GetProperty("success").GetBoolean()) throw new InvalidDataException(result.TryGetProperty("error", out var error) ? error.GetString() : "组件检查失败。");
            return result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw;
        }
    }
    private static async Task<string> BoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0) { text.Append(buffer, 0, read); if (text.Length > 1024 * 1024) throw new InvalidDataException("组件响应/日志超过1 MiB限制。"); }
        return text.ToString();
    }
}

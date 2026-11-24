using System.Text.Json;

namespace VisionPlatform.AI;

/// <summary>Code, interpreter and user data are independent; startup never installs packages.</summary>
public sealed record AiServiceSettings
{
    public string YoloRoot { get; init; } = "";
    public string PythonPath { get; init; } = "";
    public string DataHome { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "AIData");
    public string TransferRoot { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "AITransfer");
    public string ServiceScript { get; init; } = Path.Combine(AppContext.BaseDirectory, "tools", "ai_workspace_service", "service.py");
    public int RequestTimeoutMilliseconds { get; init; } = 60000;

    public static AiServiceSettings Discover()
    {
        var settings = new AiServiceSettings();
        try
        {
            var machine = new VisionPlatform.Contracts.Deployment.MachineConfigurationStore().Load();
            if (machine.TrainingEnabled) settings = settings with { YoloRoot = machine.TrainingRoot, PythonPath = machine.TrainingPython };
        }
        catch (Exception) { /* Invalid optional machine configuration must not block core startup; the setup wizard diagnoses it. */ }
        if (Environment.GetEnvironmentVariable("YOLO_STUDIO_HOME") is { Length: > 0 } home)
            settings = settings with { DataHome = Path.GetFullPath(home) };
        return settings;
    }
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Path.IsPathFullyQualified(YoloRoot) || !File.Exists(Path.Combine(YoloRoot, "core", "inference", "service.py"))) errors.Add("请选择现有 YOLOStudio 代码目录。");
        if (!Path.IsPathFullyQualified(PythonPath) || !File.Exists(PythonPath)) errors.Add("请选择现有 Python 环境中的 python.exe；不会自动安装或更换环境。");
        if (!Path.IsPathFullyQualified(ServiceScript) || !File.Exists(ServiceScript)) errors.Add("AI 业务服务脚本不存在。");
        if (!Path.IsPathFullyQualified(DataHome) || !Path.IsPathFullyQualified(TransferRoot)) errors.Add("数据目录与图像交换目录必须是绝对路径。");
        if (Path.IsPathFullyQualified(DataHome))
        {
            var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataHome));
            var programRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (dataRoot.Equals(programRoot, StringComparison.OrdinalIgnoreCase) ||
                dataRoot.StartsWith(programRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                errors.Add("AI 用户数据不能保存在发布程序目录中。");
        }
        if (RequestTimeoutMilliseconds is < 1000 or > 600000) errors.Add("服务请求超时应为 1..600 秒。");
        return errors;
    }
}

public sealed class AiServiceSettingsStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio"), "ai-workspace-v1.json");
    public async Task<AiServiceSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return AiServiceSettings.Discover();
        var saved = JsonSerializer.Deserialize<SettingsEnvelope>(await File.ReadAllTextAsync(_path, ct)) ?? throw new InvalidDataException("AI 工作区配置为空。");
        if (saved.Version != 1) throw new InvalidDataException("不支持的 AI 工作区配置版本。");
        return saved.Settings;
    }
    public async Task SaveAsync(AiServiceSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SettingsEnvelope(1, settings), new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record SettingsEnvelope(int Version, AiServiceSettings Settings);
}

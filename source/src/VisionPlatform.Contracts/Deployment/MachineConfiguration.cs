using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionPlatform.Contracts.Deployment;

/// <summary>Per-machine settings, never a portable project resource. No secrets are stored here.</summary>
public sealed record MachineConfiguration
{
    public int Version { get; init; } = 1;
    public bool SetupCompleted { get; init; }
    public string DetectionPython { get; init; } = "";
    public string DetectionComponentRoot { get; init; } = "";
    public string DetectionBackend { get; init; } = "onnx-cpu";
    public bool TrainingEnabled { get; init; }
    public string TrainingPython { get; init; } = "";
    public string TrainingRoot { get; init; } = "";
    public string BoqWorkerPath { get; init; } = "";
    public string BoqSdkPath { get; init; } = "";
    public string VendorDriverDirectory { get; init; } = "";
    public string DataDirectory { get; init; } = Local("Data");
    public string CacheDirectory { get; init; } = Local("Cache");
    public string LogDirectory { get; init; } = Local("Logs");
    private static string Local(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", name);
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Version != 1) errors.Add("不支持的机器配置版本；原配置未覆盖。");
        if (DetectionBackend != "onnx-cpu") errors.Add("检测组件当前只支持显式 ONNX CPU，不会切换 PT/CUDA。");
        foreach (var (name, path, required) in new[] { ("检测 Python", DetectionPython, false), ("检测组件", DetectionComponentRoot, false),
            ("训练 Python", TrainingPython, TrainingEnabled), ("训练工作区", TrainingRoot, TrainingEnabled),
            ("博峤 Worker", BoqWorkerPath, false), ("博峤 SDK", BoqSdkPath, false), ("厂商驱动目录", VendorDriverDirectory, false),
            ("数据目录", DataDirectory, true), ("缓存目录", CacheDirectory, true), ("日志目录", LogDirectory, true) })
        {
            if (string.IsNullOrWhiteSpace(path)) { if (required) errors.Add(name + "尚未配置。"); continue; }
            if (!Path.IsPathFullyQualified(path)) errors.Add(name + "必须是完整绝对路径。");
        }
        foreach (var path in new[] { DataDirectory, CacheDirectory, LogDirectory })
            if (Path.IsPathFullyQualified(path) && IsWithin(AppContext.BaseDirectory, path)) errors.Add("数据、缓存和日志应在程序目录以外，升级不覆盖用户数据。");
        return errors;
    }
    public static bool IsWithin(string root, string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed class MachineConfigurationStore(string? path = null)
{
    public string FilePath { get; } = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "machine-v1.json"));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public MachineConfiguration Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<MachineConfiguration>(File.ReadAllText(FilePath), Json) ?? throw new InvalidDataException("机器配置为空。");
        var errors = settings.Validate(); if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        return settings;
    }
    public async Task SaveAsync(MachineConfiguration settings, CancellationToken ct = default)
    {
        var errors = settings.Validate(); if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, Json), ct);
            ct.ThrowIfCancellationRequested();
            // Retain the previous machine setup for rollback; never touch the external environment.
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".previous"); else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

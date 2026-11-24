using System.Text.Json;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Editor.Core.Workspace;

public sealed record WorkspaceSettings
{
    public string ThemeId { get; init; } = "LightBlue";
    public string Language { get; init; } = "zh-CN";
    public string DefaultProjectDirectory { get; init; } = MachineDefault(true);
    public string ExamplesDirectory { get; init; } = FindExamples();
    public string LogDirectory { get; init; } = MachineDefault(false);
    private static string MachineDefault(bool projects)
    {
        try { var m = new VisionPlatform.Contracts.Deployment.MachineConfigurationStore().Load(); return projects ? Path.Combine(m.DataDirectory, "Projects") : m.LogDirectory; }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", projects ? "Projects" : "Logs"); }
    }
    public bool LogEnabled { get; init; } = true;
    public int ResultImageBudgetMiB { get; init; } = 512;
    public static string FindExamples()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "Examples");
            if (Directory.Exists(path)) return path;
            if (File.Exists(Path.Combine(current.FullName, "VisionPlatform.sln"))) break;
            current = current.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "Examples");
    }
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ThemeId != "LightBlue") errors.Add("本批次仅提供浅蓝主题，深色主题接口保留。");
        if (Language != "zh-CN") errors.Add("本批次仅提供中文。");
        foreach (var (name, path) in new[] { ("默认工程目录", DefaultProjectDirectory), ("示例目录", ExamplesDirectory), ("日志目录", LogDirectory) })
        {
            try { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) errors.Add(name + "必须是绝对路径。"); else _ = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { errors.Add(name + "无效。"); }
        }
        if (ResultImageBudgetMiB is < 64 or > 4096) errors.Add("结果图像预算必须为64..4096 MiB。");
        return errors;
    }
}

public sealed record RecentProjectEntry(string Path, string Name, DateTimeOffset LastOpened)
{
    public bool Exists => File.Exists(Path);
    public string Location => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string Availability => Exists ? "可打开" : "文件不存在";
}
public sealed record WorkspaceState(int SchemaVersion, WorkspaceSettings Settings, IReadOnlyList<RecentProjectEntry> RecentProjects);

/// <summary>User-local editor sidecar. It never changes ProjectDocument or runs algorithms.</summary>
public sealed class WorkspaceStateStore
{
    public const int MaximumRecentProjects = 20;
    public string FilePath { get; }
    public WorkspaceStateStore(string? directory = null)
    {
        FilePath = Path.Combine(Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio")), "workspace-v1.json");
    }
    public async Task<WorkspaceState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return new(1, new(), []);
        // A reader keeps its original file snapshot while a different workspace atomically replaces it.
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        var state = await JsonSerializer.DeserializeAsync<WorkspaceState>(stream, JsonDefaults.Options, ct).ConfigureAwait(false) ?? throw new InvalidDataException("工作区设置为空。");
        if (state.SchemaVersion != 1 || state.Settings is null || state.Settings.Validate().Count != 0 || state.RecentProjects is null)
            throw new InvalidDataException("工作区设置版本或参数不受支持，未覆盖原文件。");
        var recent = state.RecentProjects.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Path) && Path.IsPathFullyQualified(p.Path))
            .DistinctBy(p => Path.GetFullPath(p.Path), StringComparer.OrdinalIgnoreCase).Take(MaximumRecentProjects).ToArray();
        return state with { RecentProjects = recent };
    }
    public async Task SaveAsync(WorkspaceState state, CancellationToken ct = default)
    {
        var errors = state.Settings.Validate();
        if (state.SchemaVersion != 1 || errors.Count != 0) throw new ArgumentException(string.Join("; ", errors));
        var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, ".workspace-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var snapshot = state with { RecentProjects = state.RecentProjects.Take(MaximumRecentProjects).ToArray() };
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonDefaults.Options), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

using System.Text.Json;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Editor.Core.Persistence;

public sealed record EditorNodeLayout(string NodeId, double X, double Y);
public sealed record EditorLayout(int SchemaVersion, Guid ProjectId, IReadOnlyList<EditorNodeLayout> Nodes);

/// <summary>View state only. Algorithm parameters and ROI never enter this sidecar.</summary>
public static class EditorLayoutStore
{
    public static string SidecarPath(string projectPath) => projectPath + ".editor.json";
    public static async Task<EditorLayout?> LoadAsync(string projectPath, Guid projectId)
    {
        var path = SidecarPath(projectPath);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<EditorLayout>(stream, JsonDefaults.Options);
            if (value is null || value.SchemaVersion != 1 || value.ProjectId != projectId || value.Nodes is null ||
                value.Nodes.Any(n => n is null || !double.IsFinite(n.X) || !double.IsFinite(n.Y)))
                throw new InvalidDataException("编辑器布局版本或数据无效。");
            return value;
        }
        catch (JsonException ex) { throw new InvalidDataException("编辑器布局 JSON 无效。", ex); }
    }
    public static async Task SaveAsync(string projectPath, EditorLayout layout)
    {
        var path = Path.GetFullPath(SidecarPath(projectPath));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, layout, JsonDefaults.Options);
                await stream.FlushAsync(); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

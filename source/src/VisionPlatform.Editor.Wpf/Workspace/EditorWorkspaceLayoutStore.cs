using System.IO;
using System.Text.Json;
namespace VisionPlatform.Editor.Wpf.Workspace;

public sealed record EditorWorkspaceLayout(int SchemaVersion = 1, double InspectorWidth = 315,
    double FlowShare = 1.05, double ImageShare = 1, string ViewMode = "Both");

/// <summary>Versioned editor-only layout; project image coordinates and graph data are never changed.</summary>
public sealed class EditorWorkspaceLayoutStore(string? directory)
{
    public string FilePath { get; } = Path.Combine(Path.GetFullPath(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio")), "editor-workspace-v1.json");
    public async Task<EditorWorkspaceLayout> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new();
        await using var stream = File.OpenRead(FilePath);
        var layout = await JsonSerializer.DeserializeAsync<EditorWorkspaceLayout>(stream) ?? throw new InvalidDataException("编辑器布局为空。");
        Validate(layout); return layout;
    }
    public async Task SaveAsync(EditorWorkspaceLayout layout)
    {
        Validate(layout);
        var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".editor-layout-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(layout));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(EditorWorkspaceLayout layout)
    {
        if (layout.SchemaVersion != 1 || !double.IsFinite(layout.InspectorWidth) || layout.InspectorWidth is < 240 or > 1000 ||
            !double.IsFinite(layout.FlowShare) || layout.FlowShare <= 0 || !double.IsFinite(layout.ImageShare) || layout.ImageShare <= 0 ||
            layout.ViewMode is not ("Both" or "Flow" or "Image")) throw new InvalidDataException("编辑器布局版本或尺寸不受支持。");
    }
}

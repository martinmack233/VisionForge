using System.Text.Json;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Persistence;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Session;

/// <summary>Configuration only: never contains execution values, native buffers or device sessions.</summary>
public sealed record EditorClipboard(Guid ProjectId, string ResourceRoot,
    IReadOnlyList<NodeSpecification> Nodes, IReadOnlyList<NodeConnection> Connections,
    IReadOnlyList<EditorNodeLayout> Locations, IReadOnlyList<RotatedRectRoi> Roi,
    IReadOnlyList<TemplateResourceReference> Templates, IReadOnlyDictionary<string, string> Algorithms);

public sealed partial class EditorSession
{
    private sealed record EditSnapshot(ProjectDocument Document, EditorNodeLayout[] Layout, string Root, string? InspectionId);
    private readonly List<(string Name, EditSnapshot State)> _undo = [], _redo = [];
    private EditSnapshot? _editStart;
    private string _editName = "编辑";
    private int _editDepth;
    private bool _restoringHistory;
    private const int HistoryLimit = 80;
    public bool CanUndo => _undo.Count > 0 && !_blocking;
    public bool CanRedo => _redo.Count > 0 && !_blocking;

    public IDisposable BeginEdit(string name)
    {
        CheckEditable();
        if (_restoringHistory) return new EmptyEditScope();
        if (_editDepth++ == 0) { _editStart = CaptureEdit(); _editName = name; }
        return new EditScope(this);
    }
    private sealed class EditScope(EditorSession owner) : IDisposable
    {
        private EditorSession? _owner = owner;
        public void Dispose() { var session = Interlocked.Exchange(ref _owner, null); session?.EndEdit(); }
    }
    private EditSnapshot CaptureEdit() => new(SnapshotProject(), Nodes.Select(n => new EditorNodeLayout(n.NodeId, n.X, n.Y)).ToArray(), ResourceRoot, SelectedNode?.NodeId);
    private static string EditKey(EditSnapshot state) => JsonSerializer.Serialize(new { state.Document, state.Layout }, JsonDefaults.Options);
    private void EndEdit()
    {
        if (_editDepth <= 0 || --_editDepth != 0) return;
        var before = _editStart; _editStart = null;
        if (_disposed || before is null || EditKey(before) == EditKey(CaptureEdit())) return;
        _undo.Add((_editName, before)); if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        _redo.Clear(); NotifyHistory();
    }
    private void NotifyHistory() { Notify(nameof(CanUndo)); Notify(nameof(CanRedo)); }
    private void ResetHistory() { _undo.Clear(); _redo.Clear(); _editStart = null; _editDepth = 0; NotifyHistory(); }
    public void Undo() => RestoreFromHistory(_undo, _redo, "撤销");
    public void Redo() => RestoreFromHistory(_redo, _undo, "重做");
    private void RestoreFromHistory(List<(string Name, EditSnapshot State)> from, List<(string Name, EditSnapshot State)> to, string operation)
    {
        CheckEditable(); if (from.Count == 0) return;
        if (_editDepth != 0) throw new InvalidOperationException("请先完成当前拖动或编辑操作。");
        var entry = from[^1]; from.RemoveAt(from.Count - 1); to.Add((entry.Name, CaptureEdit()));
        _restoringHistory = true;
        try
        {
            Cancel();
            var snapshot = Rebase(entry.State.Document, entry.State.Root, ResourceRoot);
            var currentSources = JsonSerializer.Serialize(Nodes.Where(n => n.TypeId is "ImageInput" or "VirtualCamera" or "CameraInput").Select(n => n.Specification), JsonDefaults.Options);
            var restoredSources = JsonSerializer.Serialize(snapshot.Nodes.Where(n => n.NodeTypeId is "ImageInput" or "VirtualCamera" or "CameraInput"), JsonDefaults.Options);
            if (currentSources != restoredSources) ReleaseSource();
            _document = snapshot; _projectName = snapshot.ProjectName;
            ReplaceGraph(snapshot, new(1, snapshot.ProjectId, entry.State.Layout));
            SelectedNode = Nodes.FirstOrDefault(n => n.NodeId == entry.State.InspectionId) ?? Nodes.FirstOrDefault();
            SetEditedRoi(snapshot.ROI.FirstOrDefault());
            Invalidate($"已{operation}：{entry.Name}。执行结果已失效；设备和通信不会自动执行。");
            NotifyProjectProperties();
        }
        finally { _restoringHistory = false; NotifyHistory(); }
    }

    public void RenameNode(EditorNode node, string name)
    {
        CheckEditable(); if (!Nodes.Contains(node)) throw new ArgumentException("节点不属于当前工程。");
        name = name.Trim();
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl)) throw new ArgumentException("节点名称需要 1–128 个可打印字符。");
        using var edit = BeginEdit("重命名节点");
        node.ApplySpecification(node.Specification with { Name = name }); Invalidate("节点已重命名。");
    }
    public void SetNodeEnabled(EditorNode node, bool enabled)
    {
        CheckEditable(); if (!Nodes.Contains(node)) throw new ArgumentException("节点不属于当前工程。");
        if (node.IsEnabled == enabled) return;
        using var edit = BeginEdit(enabled ? "启用算子" : "禁用算子");
        if (!enabled)
            foreach (var edge in Connections.Where(e => e.Source.Node == node || e.Target.Node == node).ToArray()) Connections.Remove(edge);
        node.ApplySpecification(node.Specification with { IsEnabled = enabled });
        Invalidate(enabled ? "算子已启用；已断开的连线需手动重新连接。" : "算子已禁用，全部数据线及控制线已断开；受影响下游需重新连接。撤销可恢复本次原连线。");
    }
    public EditorClipboard CopyNodes(IEnumerable<EditorNode> nodes)
    {
        CheckAlive(); var selected = nodes.Distinct().ToArray();
        if (selected.Any(n => !Nodes.Contains(n))) throw new ArgumentException("复制对象不属于当前工程。");
        var ids = selected.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
        var specs = selected.Select(n => n.Specification with { Parameters = n.Parameters.Clone() }).ToArray();
        var templatePaths = specs.Where(n => n.NodeTypeId == "TemplateMatching")
            .Select(n => n.Parameters.TryGetProperty("TemplatePath", out var p) ? p.GetString() : null).Where(p => p is not null).ToHashSet();
        return new(_document.ProjectId, ResourceRoot, specs,
            Connections.Where(c => ids.Contains(c.Source.Node.NodeId) && ids.Contains(c.Target.Node.NodeId)).Select(c => c.ToContract()).ToArray(),
            selected.Select(n => new EditorNodeLayout(n.NodeId, n.X, n.Y)).ToArray(), _document.ROI.ToArray(),
            _document.TemplateResources.Where(r => ids.Contains(r.ResourceId) || templatePaths.Contains(r.Path)).ToArray(),
            new Dictionary<string, string>(_document.AlgorithmVersion, StringComparer.Ordinal));
    }
    public IReadOnlyList<EditorNode> PasteNodes(EditorClipboard clipboard, double offsetX = 40, double offsetY = 40)
    {
        CheckEditable(); ArgumentNullException.ThrowIfNull(clipboard);
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(offsetX));
        if (clipboard.Nodes.Count == 0) return [];
        var crossProject = clipboard.ProjectId != _document.ProjectId || !string.Equals(clipboard.ResourceRoot, ResourceRoot, StringComparison.OrdinalIgnoreCase);
        if (crossProject && (clipboard.Templates.Count > 0 || clipboard.Nodes.Any(n => HasFileDependency(n.Parameters))))
            throw new InvalidOperationException("跨工程粘贴包含图像、模板或目录资源引用。请先将资源复制到目标工程并重新指定；当前仅支持同工程完整资源引用复制，或跨工程复制无文件依赖的节点。");
        var descriptors = clipboard.Nodes.ToDictionary(n => n.NodeId, n => Registry.Create(n.NodeTypeId).Descriptor, StringComparer.Ordinal);
        var ids = clipboard.Nodes.ToDictionary(n => n.NodeId, n => n.NodeTypeId.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..12], StringComparer.Ordinal);
        using var edit = BeginEdit("粘贴节点"); var pasted = new List<EditorNode>();
        foreach (var spec in clipboard.Nodes)
        {
            var point = clipboard.Locations.FirstOrDefault(l => l.NodeId == spec.NodeId);
            var clone = new EditorNode(spec with { NodeId = ids[spec.NodeId], Parameters = spec.Parameters.Clone() }, descriptors[spec.NodeId],
                (point?.X ?? 0) + offsetX, (point?.Y ?? 0) + offsetY);
            Nodes.Add(clone); pasted.Add(clone);
        }
        foreach (var edge in clipboard.Connections)
        {
            var source = pasted.Single(n => n.NodeId == ids[edge.SourceNodeId]); var target = pasted.Single(n => n.NodeId == ids[edge.TargetNodeId]);
            if (!source.IsEnabled || !target.IsEnabled) continue;
            Connections.Add(new(source.AllOutputs.Single(p => p.Name == edge.SourcePort), target.AllInputs.Single(p => p.Name == edge.TargetPort)));
        }
        foreach (var template in clipboard.Templates)
        {
            var resource = template with { ResourceId = ids.GetValueOrDefault(template.ResourceId) ?? template.ResourceId };
            if (!_document.TemplateResources.Any(r => r.ResourceId == resource.ResourceId)) _document.TemplateResources.Add(resource);
        }
        if (crossProject)
            foreach (var roi in clipboard.Roi) if (!_document.ROI.Contains(roi)) _document.ROI.Add(roi);
        foreach (var pair in clipboard.Algorithms) _document.AlgorithmVersion[pair.Key] = pair.Value;
        SelectedNode = pasted.First(); Invalidate("配置节点已粘贴；新 ID 和内部连线已生成，未复制执行结果、图像缓冲或设备连接。");
        return pasted;
    }
    private static bool HasFileDependency(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()) &&
                    (property.Name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) || property.Name.EndsWith("Directory", StringComparison.OrdinalIgnoreCase))) return true;
                if (HasFileDependency(property.Value)) return true;
            }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(HasFileDependency);
        return false;
    }
}

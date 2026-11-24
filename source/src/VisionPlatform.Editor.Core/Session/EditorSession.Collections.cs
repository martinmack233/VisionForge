using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Session;
public sealed partial class EditorSession
{
    private CollectionDisplayResult? _collectionDisplay;
    private VisionItem? _collectionSelection;
    public CollectionDisplayResult? CollectionDisplay => _collectionDisplay;
    public VisionItem? CollectionSelection => _collectionSelection;
    private void RefreshCollectionDisplay(object? selected = null)
    {
        _collectionSelection = null; Notify(nameof(CollectionSelection));
        var values = SelectedOutputs.Select(o => o.Value);
        _collectionDisplay = selected as CollectionDisplayResult ?? (selected is VisionCollection c ? new(c, new()) :
            values.OfType<CollectionDisplayResult>().FirstOrDefault() ?? (values.OfType<VisionCollection>().FirstOrDefault() is { } collection ? new(collection, new()) : null));
        Notify(nameof(CollectionDisplay));
    }
    public bool SelectCollectionItem(VisionItem? item)
    {
        if (item is not null && (_collectionDisplay is null || !_collectionDisplay.Collection.Items.Contains(item))) return false;
        _collectionSelection = item; Notify(nameof(CollectionSelection));
        if (item is not null) SetStatus($"来源 {item.Origin.Key} · {item.Kind} · {item.State} · {item.Origin.CoordinateSpaceId}");
        return true;
    }
    public IReadOnlyList<IterationTrace> IterationTraces => SelectedOutputs.Select(o => o.Value).OfType<IterationTraceStore>().FirstOrDefault()?.Traces ?? [];
    public bool InspectIterationOutput(int index, string nodeId, string port)
    {
        var value = IterationTraces.FirstOrDefault(t => t.Index == index)?.Nodes.FirstOrDefault(n => n.NodeId == nodeId)?.Outputs.GetValueOrDefault(port);
        if (value is null) { SetStatus("此迭代端口未保留调试快照；请增大调试预算后重跑，结果不会从旧会话补齐。"); return false; }
        _selectedOutputInspection = OutputInspector.Describe(new(nodeId, port, Summarize(value), value)); Notify(nameof(SelectedOutputInspection));
        if (value is ImageFrame image) SetDisplay(image);
        if (value is VisionCollection or CollectionDisplayResult) RefreshCollectionDisplay(value); SetStatus($"迭代 {index} · {nodeId}.{port} · {Summarize(value)}"); return true;
    }
    /// <summary>Detached authoring only: no source node, device or algorithm is executed.</summary>
    public async Task LoadSubflowForEditingAsync(SubflowDefinition body, string resourceRoot)
    {
        await NewAsync(); _document = new ProjectDocument { ProjectName = "逐项子流程", Nodes = body.Nodes, Connections = body.Connections };
        _projectName = _document.ProjectName; _resourceRoot = Path.GetFullPath(resourceRoot); ReplaceGraph(_document, null);
        foreach (var node in Nodes) if (body.NodeLocations.TryGetValue(node.NodeId, out var point)) { node.X = point.X; node.Y = point.Y; }
        SetChanges(false); NotifyProjectProperties();
    }
}

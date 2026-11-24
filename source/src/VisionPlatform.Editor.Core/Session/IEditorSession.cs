using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using VisionPlatform.Acquisition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Session;

public enum EditorRunMode { All, ToSelected, Single }

/// <summary>Dispatcher-neutral editor boundary. Session is UI-thread-affine; native work runs off that thread.</summary>
public interface IEditorSession : INotifyPropertyChanged, IDisposable
{
    CameraSession? CameraSession => null;
    long AcquisitionRevision => 0;
    NodeRegistry Registry { get; }
    NodeAuthoringCatalog Catalog { get; }
    ObservableCollection<EditorNode> Nodes { get; }
    ObservableCollection<EditorConnection> Connections { get; }
    ObservableCollection<NodeResultRow> Results { get; }
    ObservableCollection<OutputValueRow> SelectedOutputs { get; }
    EditorNode? SelectedNode { get; set; }
    ImageFrame? DisplayImage { get; }
    InspectionReport? CurrentInspectionReport => null;
    OutputInspection? SelectedOutputInspection => null;
    /// <summary>Borrowed current-execution values. Consumers must not dispose image values or keep them after invalidation.</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> CurrentOutputs => new ReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>(new Dictionary<string, IReadOnlyDictionary<string, object>>());
    IReadOnlyList<MeasurementOverlay> MeasurementOverlays => [];
    IReadOnlyList<RecognitionOverlay> RecognitionOverlays => [];
    bool InspectOutput(string nodeId, string portName)
    {
        var node = Nodes.FirstOrDefault(n => n.NodeId == nodeId); if (node is null) return false;
        SelectedNode = node; var output = SelectedOutputs.FirstOrDefault(o => o.PortName == portName);
        if (output is null) { SetStatus("该端口没有本次有效输出，请先运行节点。"); return false; }
        SelectOutput(output); return true;
    }
    IReadOnlyList<OverlayItem> Overlays { get; }
    RotatedRectRoi? EditedRoi { get; }
    string ProjectName { get; set; }
    string? ProjectPath { get; }
    string ResourceRoot { get; }
    string StatusMessage { get; }
    bool IsBusy { get; }
    bool HasChanges { get; }
    long Revision { get; }
    long RetainedImageBytes { get; }
    long ResultImageBudgetBytes { get; }
    void AddNode(string typeId, double x = 0, double y = 0);
    void MoveNode(EditorNode node, double x, double y);
    void RemoveNode(EditorNode node);
    bool CanUndo => false;
    bool CanRedo => false;
    IDisposable BeginEdit(string name) => new EmptyEditScope();
    void Undo() { }
    void Redo() { }
    void RenameNode(EditorNode node, string name) => throw new NotSupportedException();
    void SetNodeEnabled(EditorNode node, bool enabled) => throw new NotSupportedException();
    void SetAiProjectLink(AiProjectLink? link) => throw new NotSupportedException("当前编辑器不支持 AI 项目关联。");
    EditorClipboard CopyNodes(IEnumerable<EditorNode> nodes) => throw new NotSupportedException();
    IReadOnlyList<EditorNode> PasteNodes(EditorClipboard clipboard, double offsetX = 40, double offsetY = 40) => throw new NotSupportedException();
    bool TryConnect(EditorPort source, EditorPort target, out string? reason);
    void Disconnect(EditorConnection connection);
    void CommitParameters(EditorNode node, JsonElement parameters);
    void UpdateRoi(RotatedRectRoi roi);
    void BindRoiToSelectedNode();
    void SelectOutput(OutputValueRow output);
    void SetStatus(string message);
    void Cancel();
    Task NewAsync();
    Task OpenAsync(string path);
    /// <summary>Configuration-only preview for example browsing. Older hosts retain their OpenAsync fallback.</summary>
    Task OpenConfigurationAsync(string path) => OpenAsync(path);
    Task SaveAsync(string? path = null);
    Task ImportImageAsync(string path, PixelFormat? format = null);
    Task CreateDemoAsync(string directory);
    Task RunAsync(EditorRunMode mode = EditorRunMode.All);
    Task RunWithFrameAsync(string sourceNodeId, ImageFrame frame, CameraFrameInfo? info = null) => throw new NotSupportedException("当前编辑器不支持冻结帧检测。");
    Task TeachTemplateAsync();
    Task CloseAsync();
    ProjectDocument SnapshotProject();
}

internal sealed class EmptyEditScope : IDisposable { public void Dispose() { } }

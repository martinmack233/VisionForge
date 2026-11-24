using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Graph;

public sealed class GraphPortViewModel(EditorPort port) : ObservableObject
{
    private Point _anchor;
    private bool _connected;
    public EditorPort Port { get; } = port;
    public bool IsEnabled => Port.Node.IsEnabled;
    public bool IsControl => Port.IsControl;
    public string DisplayName => Port.Name == NodePorts.Execute ? "▶ 执行" : Port.Name == NodePorts.Completed ? "▶ 完成" : IsControl ? "▶ " + Port.Name : Port.Name;
    public Point Anchor { get => _anchor; set => Set(ref _anchor, value); }
    public bool IsConnected { get => _connected; set => Set(ref _connected, value); }
    public string Label => $"{Port.Node.Name} [{Port.Node.NodeId}].{Port.Name} : {Port.TypeName}";
    public void RefreshEnabled() => Notify(nameof(IsEnabled));
}

public sealed class GraphNodeViewModel : ObservableObject, IDisposable
{
    private readonly IEditorSession _session;
    private bool _selected;
    private readonly bool _multiSelection;
    public EditorNode Node { get; }
    public string DisplayName
    {
        get
        {
            if (Node.Name != Node.TypeId) return Node.Name;
            try { return _session.Catalog.Get(Node.TypeId).DisplayName; }
            catch (KeyNotFoundException) { return Node.Name; }
        }
    }
    public IReadOnlyList<GraphPortViewModel> Inputs { get; }
    public IReadOnlyList<GraphPortViewModel> Outputs { get; }
    public Point Location { get => new(Node.X, Node.Y); set { if (value == Location) return; _session.MoveNode(Node, value.X, value.Y); Notify(); } }
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value && _session.SelectedNode != Node) _session.SelectedNode = Node;
            else if (!value && !_multiSelection && _session.SelectedNode == Node) _session.SelectedNode = null;
        }
    }
    public GraphNodeViewModel(EditorNode node, IEditorSession session, bool multiSelection = false)
    {
        Node = node; _session = session; _multiSelection = multiSelection;
        Inputs = node.AllInputs.Select(p => new GraphPortViewModel(p)).ToArray();
        Outputs = node.AllOutputs.Select(p => new GraphPortViewModel(p)).ToArray();
        node.PropertyChanged += NodeChanged;
    }
    private void NodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorNode.X) or nameof(EditorNode.Y)) Notify(nameof(Location));
        if (e.PropertyName == nameof(EditorNode.Name)) Notify(nameof(DisplayName));
        if (e.PropertyName == nameof(EditorNode.IsEnabled))
            foreach (var port in Inputs.Concat(Outputs)) port.RefreshEnabled();
    }
    public void Dispose() => Node.PropertyChanged -= NodeChanged;
}
public sealed class GraphConnectionViewModel(EditorConnection connection, GraphPortViewModel source, GraphPortViewModel target) : ObservableObject
{
    private bool _selected;
    public EditorConnection Connection { get; } = connection;
    public GraphPortViewModel Source { get; } = source;
    public GraphPortViewModel Target { get; } = target;
    public string Label => Connection.Label;
    public bool IsControl => Source.IsControl;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}
public sealed class PendingGraphConnection : ObservableObject
{
    private GraphPortViewModel? _source, _target;
    private bool _isVisible;
    public GraphPortViewModel? Source { get => _source; set => Set(ref _source, value); }
    public GraphPortViewModel? Target { get => _target; set => Set(ref _target, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
}

/// <summary>Nodify presentation only. All mutation and validation is delegated to IEditorSession.</summary>
public sealed class GraphEditorState : ObservableObject, IDisposable
{
    private IEditorSession? _session;
    private string _message = "类型、循环和重复输入将在连接时验证。";
    private string _searchText = "";
    private string _selectedCategory = "全部算法";
    private readonly bool _multiSelection;
    private IDisposable? _moveEdit;
    private static EditorClipboard? _clipboard;
    private int _pasteCount;
    public IReadOnlyList<GraphNodeViewModel> SelectedNodes => Nodes.Where(n => n.IsSelected).ToArray();
    public bool HasToolboxResults => Toolbox.Count > 0;
    private static readonly string[] CategoryOrder = ["图像输入", "图像预处理", "图像分割", "图像变换", "目标检测", "模板匹配", "ROI 工具", "逻辑控制", "通信", "结果显示"];
    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<GraphPortViewModel> InputPorts { get; } = [];
    public ObservableCollection<GraphPortViewModel> OutputPorts { get; } = [];
    public IReadOnlyList<GraphPortViewModel> AllInputPorts => Nodes.SelectMany(n => n.Inputs).ToArray();
    public IReadOnlyList<GraphPortViewModel> AllOutputPorts => Nodes.SelectMany(n => n.Outputs).ToArray();
    public IReadOnlyList<string> Categories => new[] { "全部算法" }.Concat((_session?.Catalog.Items ?? [])
        .Select(item => item.Category).Distinct().OrderBy(category => CategoryIndex(category)).ThenBy(category => category)).ToArray();
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) { Notify(nameof(Toolbox)); Notify(nameof(HasToolboxResults)); } }
    }
    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (Set(ref _selectedCategory, value ?? "全部算法")) { Notify(nameof(Toolbox)); Notify(nameof(HasToolboxResults)); } }
    }
    public IReadOnlyList<NodeAuthoringDefinition> Toolbox => (_session?.Catalog.Items ?? [])
        .Where(item => _allowedTypes is null || _allowedTypes.Contains(item.TypeId))
        .Where(item => SelectedCategory == "全部算法" || item.Category == SelectedCategory)
        .Where(item => string.IsNullOrWhiteSpace(SearchText) ||
            item.DisplayName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.TypeId.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => CategoryIndex(item.Category)).ThenBy(item => item.DisplayName).ToArray();
    private HashSet<string>? _allowedTypes;
    public void RestrictToolbox(IEnumerable<string> types)
    { _allowedTypes = types.ToHashSet(StringComparer.Ordinal); Notify(nameof(Toolbox)); Notify(nameof(HasToolboxResults)); }
    private static int CategoryIndex(string category)
    {
        var index = Array.IndexOf(CategoryOrder, category);
        return index < 0 ? CategoryOrder.Length : index;
    }
    public PendingGraphConnection Pending { get; } = new();
    public string ConnectionMessage { get => _message; private set => Set(ref _message, value); }
    public ICommand StartConnectionCommand { get; }
    public ICommand CompleteConnectionCommand { get; }
    public ICommand DisconnectPortCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DragStartedCommand { get; }
    public ICommand DragCompletedCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public GraphEditorState(bool multiSelection = false)
    {
        _multiSelection = multiSelection;
        CopyCommand = new GraphCommand(_ => Copy());
        PasteCommand = new GraphCommand(_ => Paste());
        CutCommand = new GraphCommand(_ => { Copy(); DeleteSelection("剪切节点"); });
        DuplicateCommand = new GraphCommand(_ => { Copy(); Paste(); });
        UndoCommand = new GraphCommand(_ => _session?.Undo(), _ => _session?.CanUndo == true);
        RedoCommand = new GraphCommand(_ => _session?.Redo(), _ => _session?.CanRedo == true);
        SelectAllCommand = new GraphCommand(_ => { foreach (var node in Nodes) node.IsSelected = true; });
        ClearSearchCommand = new GraphCommand(_ => SearchText = "");
        DragStartedCommand = new GraphCommand(_ => { _moveEdit?.Dispose(); _moveEdit = _session?.BeginEdit("移动节点"); });
        DragCompletedCommand = new GraphCommand(_ => { _moveEdit?.Dispose(); _moveEdit = null; });
        StartConnectionCommand = new GraphCommand(value =>
        {
            if (value is GraphPortViewModel { IsEnabled: false }) { ShowMessage("已禁用节点不能连接端口。"); return; }
            if (value is GraphPortViewModel source) Pending.Source = source;
            Pending.Target = null;
            Pending.IsVisible = true;
        });
        CompleteConnectionCommand = new GraphCommand(value =>
        {
            // Nodify supplies the connector's DataContext as the command parameter. Consume it directly,
            // so the user gesture does not depend on the order of OneWayToSource binding propagation.
            Pending.Target = value as GraphPortViewModel;
            Pending.IsVisible = false;
            // Releasing on the starting connector is a click/cancel, including output inspection.
            // Other invalid endpoints still go through normal connection validation.
            if (Pending.Source is { } source && Pending.Target is { } target && !ReferenceEquals(source, target)) Connect(source, target);
            Pending.Source = null;
            Pending.Target = null;
        });
        DisconnectPortCommand = new GraphCommand(value =>
        {
            if (_session is null || value is not GraphPortViewModel port) return;
            using var edit = _session.BeginEdit("断开端口连接");
            foreach (var wire in _session.Connections.Where(c => c.Source == port.Port || c.Target == port.Port).ToArray()) _session.Disconnect(wire);
        });
        RemoveConnectionCommand = new GraphCommand(value => { if (value is GraphConnectionViewModel wire) Disconnect(wire); });
        DeleteCommand = new GraphCommand(_ => DeleteSelection("删除选中对象"));
    }
    public void Attach(IEditorSession? session)
    {
        _moveEdit?.Dispose(); _moveEdit = null;
        if (_session != null)
        {
            _session.Nodes.CollectionChanged -= StructureChanged;
            _session.Connections.CollectionChanged -= ConnectionsChanged;
            _session.PropertyChanged -= SessionChanged;
        }
        foreach (var node in Nodes) node.Dispose();
        Nodes.Clear(); Connections.Clear(); InputPorts.Clear(); OutputPorts.Clear();
        Pending.IsVisible = false;
        Pending.Source = null;
        Pending.Target = null;
        _session = session;
        if (session != null)
        {
            session.Nodes.CollectionChanged += StructureChanged;
            session.Connections.CollectionChanged += ConnectionsChanged;
            session.PropertyChanged += SessionChanged;
        }
        RefreshNodes();
        Notify(nameof(HasToolboxResults));
        if (!Categories.Contains(SelectedCategory)) SelectedCategory = "全部算法";
        Notify(nameof(Categories)); Notify(nameof(Toolbox)); Notify(nameof(AllInputPorts)); Notify(nameof(AllOutputPorts));
    }
    private void StructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshNodes();
    private void ConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshConnections();
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSession.SelectedNode))
            foreach (var node in Nodes)
                if (!_multiSelection || node.Node == _session?.SelectedNode) node.IsSelected = node.Node == _session?.SelectedNode;
    }
    private void RefreshNodes()
    {
        var alive = _session?.Nodes.ToHashSet() ?? [];
        foreach (var item in Nodes.Where(n => !alive.Contains(n.Node)).ToArray()) { item.Dispose(); Nodes.Remove(item); }
        if (_session != null)
            foreach (var node in _session.Nodes)
                if (!Nodes.Any(n => n.Node == node)) Nodes.Add(new(node, _session, _multiSelection));
        InputPorts.Clear(); OutputPorts.Clear();
        foreach (var node in Nodes)
        {
            foreach (var input in node.Inputs.Where(p => !p.IsControl)) InputPorts.Add(input);
            foreach (var output in node.Outputs.Where(p => !p.IsControl)) OutputPorts.Add(output);
            if (!_multiSelection || node.Node == _session?.SelectedNode) node.IsSelected = node.Node == _session?.SelectedNode;
        }
        RefreshConnections();
        Notify(nameof(AllInputPorts)); Notify(nameof(AllOutputPorts));
    }
    private void RefreshConnections()
    {
        Connections.Clear();
        foreach (var port in AllInputPorts.Concat(AllOutputPorts)) port.IsConnected = false;
        if (_session is null) return;
        foreach (var wire in _session.Connections)
        {
            var source = AllOutputPorts.FirstOrDefault(p => p.Port == wire.Source);
            var target = AllInputPorts.FirstOrDefault(p => p.Port == wire.Target);
            if (source == null || target == null) continue;
            source.IsConnected = target.IsConnected = true;
            Connections.Add(new(wire, source, target));
        }
    }
    public bool Connect(GraphPortViewModel a, GraphPortViewModel b)
    {
        if (_session is null) return false;
        var source = a.Port.IsInput ? b.Port : a.Port;
        var target = a.Port.IsInput ? a.Port : b.Port;
        try
        {
            var accepted = _session.TryConnect(source, target, out var reason);
            ShowMessage(accepted ? "连接已建立，旧结果已失效。" : reason ?? "无法建立此连接。");
            return accepted;
        }
        catch (Exception ex) { ShowMessage(ex.Message); return false; }
    }
    public void Disconnect(GraphConnectionViewModel wire)
    {
        _session?.Disconnect(wire.Connection); ShowMessage("连接已断开，旧结果已失效。");
    }
    public void ShowMessage(string message) { ConnectionMessage = message; _session?.SetStatus(message); }
    public void Copy()
    {
        if (_session is null || SelectedNodes.Count == 0) return;
        _clipboard = _session.CopyNodes(SelectedNodes.Select(n => n.Node)); _pasteCount = 0;
        ShowMessage($"已复制 {_clipboard.Nodes.Count} 个配置节点（不含运行资源）。");
    }
    public void Paste()
    {
        if (_session is null || _clipboard is null) { ShowMessage("没有可粘贴的节点配置。"); return; }
        var pasted = _session.PasteNodes(_clipboard, 40 * (++_pasteCount), 40 * _pasteCount).ToHashSet();
        foreach (var node in Nodes) node.IsSelected = pasted.Contains(node.Node);
    }
    private void DeleteSelection(string label)
    {
        if (_session is null) return;
        using var edit = _session.BeginEdit(label);
        foreach (var wire in Connections.Where(w => w.IsSelected).ToArray()) _session.Disconnect(wire.Connection);
        foreach (var node in SelectedNodes.Select(n => n.Node).ToArray()) _session.RemoveNode(node);
    }
    public void CancelPending()
    { Pending.IsVisible = false; Pending.Source = null; Pending.Target = null; ShowMessage("临时操作已取消。"); }
    public void LayoutNodes()
    {
        if (_session is null) return;
        using var edit = _session.BeginEdit("整理布局");
        // Stable topological levels; disconnected nodes retain deterministic list order.
        var levels = Nodes.ToDictionary(n => n.Node.NodeId, _ => 0);
        for (var pass = 0; pass < Nodes.Count; pass++)
            foreach (var edge in Connections)
                levels[edge.Target.Port.Node.NodeId] = Math.Max(levels[edge.Target.Port.Node.NodeId], levels[edge.Source.Port.Node.NodeId] + 1);
        foreach (var group in Nodes.GroupBy(n => levels[n.Node.NodeId]))
        { var row = 0; foreach (var node in group) _session.MoveNode(node.Node, 50 + group.Key * 370, 50 + row++ * 220); }
    }
    public void Dispose() => Attach(null);
}
internal sealed class GraphCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Graph;

public partial class GraphEditorView : UserControl, IDisposable
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(nameof(Session), typeof(IEditorSession),
        typeof(GraphEditorView), new PropertyMetadata(null, SessionChanged));
    public IEditorSession? Session { get => (IEditorSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    public GraphEditorState State { get; } = new(multiSelection: true);
    public ICommand CopyCommand => Guard(State.CopyCommand);
    public ICommand PasteCommand => Guard(State.PasteCommand);
    public ICommand CutCommand => Guard(State.CutCommand);
    public ICommand DuplicateCommand => Guard(State.DuplicateCommand);
    public ICommand UndoCommand => Guard(State.UndoCommand);
    public ICommand RedoCommand => Guard(State.RedoCommand);
    public ICommand SelectAllCommand => Guard(State.SelectAllCommand);
    public ICommand DeleteCommand => Guard(State.DeleteCommand);
    public ICommand RenameCommand { get; }
    public ICommand EscapeCommand { get; }
    public ContextMenu? ActiveContextMenu { get; private set; }
    private ICommand Guard(ICommand command) => new GraphCommand(value =>
    { try { command.Execute(value); } catch (Exception ex) { State.ShowMessage(ex.Message); } }, command.CanExecute);
    private Point _dragStart;
    private Point _inspectionStart;
    private GraphPortViewModel? _inspectionPort;
    private GraphNodeViewModel? _inspectionNode;
    private bool _disposed;
    private Point _rightStart;
    private DependencyObject? _rightOrigin;
    private bool _rightDragged;
    public event EventHandler? NodeInspectionRequested;
    public event EventHandler? OutputInspectionRequested;
    public event EventHandler<EditorRunMode>? RunRequested;
    public GraphEditorView()
    {
        // Standalone hosts/tests may not have an Application palette. Keep that fallback local.
        if (Application.Current is null)
            Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        InitializeComponent(); Loaded += (_, _) => FitToContent();
        RenameCommand = new GraphCommand(_ => RenameSelected());
        EscapeCommand = new GraphCommand(_ => CancelGesture());
        PreviewKeyDown += GraphKeyDown;
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(InspectionMouseDown), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(InspectionMouseUp), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(ContextMouseDown), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(ContextMouseUp), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
        { if (_rightOrigin is not null && !IsInspectionClick(_rightStart, e.GetPosition(this))) _rightDragged = true; }), true);
    }
    private static void SessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (GraphEditorView)d;
        view.State.Attach((IEditorSession?)e.NewValue);
        if (view.IsLoaded) view.Dispatcher.BeginInvoke(new Action(view.FitToContent));
    }
    public void FitToContent()
    {
        if (State.Nodes.Count == 0 || NodeCanvas.ActualWidth <= 10) return;
        NodeCanvas.UpdateLayout();
        // Newly opened graphs may precede Nodify's cached ItemsExtent update. Use the
        // measured containers while retaining their original model locations.
        var extent = Rect.Empty;
        foreach (var node in State.Nodes)
            if (NodeCanvas.ItemContainerGenerator.ContainerFromItem(node) is FrameworkElement container &&
                container.ActualWidth > 0 && container.ActualHeight > 0)
                extent.Union(new Rect(node.Location, new Size(container.ActualWidth, container.ActualHeight)));
        if (!extent.IsEmpty) NodeCanvas.FitToScreen(extent);
    }
    private void FitClicked(object sender, RoutedEventArgs e) => FitToContent();
    public void FocusNode(VisionPlatform.Editor.Core.Models.EditorNode node)
    {
        if (Session is null) return; Session.SelectedNode = node;
        NodeCanvas.BringIntoView(new Point(node.X + 140, node.Y + 90), animated: false);
    }
    private void ToolboxMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(ToolboxList);
    private void ToolboxMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || ToolboxList.SelectedItem is not NodeAuthoringDefinition definition) return;
        var delta = e.GetPosition(ToolboxList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ToolboxList, new DataObject(typeof(NodeAuthoringDefinition), definition), DragDropEffects.Copy);
    }
    private void ToolboxDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ToolboxList.SelectedItem is NodeAuthoringDefinition definition)
            Session?.AddNode(definition.TypeId, NodeCanvas.ViewportLocation.X + 70, NodeCanvas.ViewportLocation.Y + 70);
    }
    private void CanvasDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(NodeAuthoringDefinition)) is not NodeAuthoringDefinition definition) return;
        var point = NodeCanvas.GetLocationInsideEditor(e);
        Session?.AddNode(definition.TypeId, point.X, point.Y); e.Handled = true;
    }
    private void ConnectClicked(object sender, RoutedEventArgs e)
    {
        if (SourcePortBox.SelectedItem is GraphPortViewModel source && TargetPortBox.SelectedItem is GraphPortViewModel target)
            State.Connect(source, target);
        else State.ShowMessage("请选择输出和输入端口。");
    }
    private void DisconnectClicked(object sender, RoutedEventArgs e)
    {
        if (ConnectionBox.SelectedItem is GraphConnectionViewModel connection) State.Disconnect(connection);
    }
    /// <summary>Keyboard focus in text entry belongs to the native text editor, never to graph mutations.</summary>
    public static bool IsTextEditingFocus(DependencyObject? focus)
    {
        while (focus is not null)
        {
            if (focus is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return true;
            focus = focus is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(focus) : LogicalTreeHelper.GetParent(focus);
        }
        return false;
    }
    private void GraphKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || IsTextEditingFocus(Keyboard.FocusedElement as DependencyObject)) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var command = ctrl ? e.Key switch
        { Key.C => "Copy", Key.V => "Paste", Key.X => "Cut", Key.D => "Duplicate", Key.Z => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "Redo" : "Undo", Key.Y => "Redo", Key.A => "SelectAll", _ => null }
            : e.Key switch { Key.Delete => "Delete", Key.F2 => "Rename", Key.Escape => "Escape", Key.Apps => "ContextMenu", Key.F10 when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) => "ContextMenu", _ => null };
        if (command is not null) e.Handled = RouteEditorCommand(command);
    }
    public bool RouteEditorCommand(string command)
    {
        var routed = command switch
        {
            "Copy" => CopyCommand, "Paste" => PasteCommand, "Cut" => CutCommand, "Duplicate" => DuplicateCommand,
            "Undo" => UndoCommand, "Redo" => RedoCommand, "SelectAll" => SelectAllCommand, "Delete" => DeleteCommand,
            "Rename" => RenameCommand, "Escape" => EscapeCommand, _ => null
        };
        try
        {
            if (routed is not null) { if (routed.CanExecute(null)) routed.Execute(null); return true; }
            if (command == "Fit") { FitToContent(); return true; }
            if (command == "Layout") { State.LayoutNodes(); FitToContent(); return true; }
            if (command == "ContextMenu") { ShowObjectMenu(null, null, State.Nodes.FirstOrDefault(n => n.Node == Session?.SelectedNode)); return true; }
        }
        catch (Exception ex) { State.ShowMessage(ex.Message); return true; }
        return false;
    }
    private void CancelGesture()
    {
        // Nodify handles Esc in its own gesture state first. Explicitly cancel editor gestures
        // as well when invoked from the shared command or from another editor panel.
        NodeCanvas.CancelDragging(); NodeCanvas.CancelSelecting(); NodeCanvas.CancelPanning();
        if (Mouse.Captured is Nodify.Connector connector) connector.CancelConnecting();
        State.CancelPending();
    }
    private void RenameSelected()
    {
        if (Session?.SelectedNode is not { } node) return;
        var dialog = new Window { Title = "重命名节点", Width = 420, Height = 172, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "StudioPanel"); dialog.SetResourceReference(ForegroundProperty, "StudioForeground");
        var stack = new StackPanel { Margin = new Thickness(18) };
        var input = new TextBox { Text = node.Name, MaxLength = 128, Margin = new Thickness(0, 0, 0, 14) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(input, "RenameNodeName");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okay = new Button { Content = "确定", IsDefault = true, MinWidth = 75 };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75 };
        okay.Click += (_, _) => { try { Session.RenameNode(node, input.Text); dialog.DialogResult = true; } catch (Exception ex) { State.ShowMessage(ex.Message); input.ToolTip = ex.Message; } };
        actions.Children.Add(okay); actions.Children.Add(cancel); stack.Children.Add(input); stack.Children.Add(actions); dialog.Content = stack;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); }; dialog.ShowDialog();
    }
    private void ContextMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || !NodeCanvas.IsMouseOver) return;
        _rightStart = e.GetPosition(this); _rightOrigin = e.OriginalSource as DependencyObject; _rightDragged = false;
    }
    private void ContextMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || _rightOrigin is null) return;
        var origin = _rightOrigin; _rightOrigin = null;
        if (_rightDragged || !IsInspectionClick(_rightStart, e.GetPosition(this))) return;
        var port = FindDataContext<GraphPortViewModel>(origin);
        var wire = FindDataContext<GraphConnectionViewModel>(origin);
        var node = FindDataContext<GraphNodeViewModel>(origin);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => ShowObjectMenu(port, wire, node)));
    }
    private void ShowObjectMenu(GraphPortViewModel? port, GraphConnectionViewModel? wire, GraphNodeViewModel? node)
    {
        if (_disposed || Session is null) return;
        if (ActiveContextMenu is { } previousMenu) previousMenu.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = NodeCanvas };
        ActiveContextMenu = menu;
        MenuItem Item(string header, Action action, string gesture = "", bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture, IsEnabled = enabled };
            item.Command = new GraphCommand(_ => { try { action(); } catch (Exception ex) { State.ShowMessage(ex.Message); } }); return item;
        }
        MenuItem Shared(string header, string command, string gesture = "") => Item(header, () => RouteEditorCommand(command), gesture);
        if (port is not null)
        {
            menu.Items.Add(Item("断开端口全部连接", () => State.DisconnectPortCommand.Execute(port)));
            menu.Items.Add(Item("查看类型：" + port.Port.TypeName, () => State.ShowMessage(port.Label)));
            menu.Items.Add(Item(port.Port.IsInput ? "定位来源" : "定位目标", () =>
            {
                var linked = State.Connections.FirstOrDefault(c => c.Source == port || c.Target == port);
                if (linked is null) { State.ShowMessage("该端口尚未连接。"); return; }
                Session.SelectedNode = (port.Port.IsInput ? linked.Source : linked.Target).Port.Node; FitToContent();
            }));
            if (!port.Port.IsInput) menu.Items.Add(Item("查看当前输出", () => InspectOutput(port.Port.Node.NodeId, port.Port.Name)));
        }
        else if (wire is not null)
        {
            foreach (var other in State.Connections) other.IsSelected = other == wire;
            menu.Items.Add(Item("断开连接", () => State.Disconnect(wire), "Delete"));
            menu.Items.Add(Item("查看类型：" + wire.Source.Port.TypeName, () => State.ShowMessage(wire.Label)));
            menu.Items.Add(Item("定位来源", () => { Session.SelectedNode = wire.Source.Port.Node; FitToContent(); }));
            menu.Items.Add(Item("定位目标", () => { Session.SelectedNode = wire.Target.Port.Node; FitToContent(); }));
        }
        else if (node is not null)
        {
            if (!node.IsSelected) foreach (var other in State.Nodes) other.IsSelected = other == node;
            Session.SelectedNode = node.Node;
            menu.Items.Add(Item("运行此节点", () => RequestRun(EditorRunMode.Single), "F6", node.Node.IsEnabled));
            menu.Items.Add(Item("运行到此处", () => RequestRun(EditorRunMode.ToSelected), enabled: node.Node.IsEnabled));
            menu.Items.Add(Item(node.Node.IsEnabled ? "禁用算子" : "启用算子", () => Session.SetNodeEnabled(node.Node, !node.Node.IsEnabled)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Shared("复制", "Copy", "Ctrl+C")); menu.Items.Add(Shared("剪切", "Cut", "Ctrl+X"));
            menu.Items.Add(Shared("复制副本", "Duplicate", "Ctrl+D")); menu.Items.Add(Shared("重命名", "Rename", "F2"));
            menu.Items.Add(Shared("删除", "Delete", "Delete")); menu.Items.Add(new Separator());
            menu.Items.Add(Item("查看输出和错误", () => { Session.SelectedNode = node.Node; OutputInspectionRequested?.Invoke(this, EventArgs.Empty); }));
        }
        else
        {
            var add = new MenuItem { Header = "添加算子" };
            foreach (var algorithm in State.Toolbox) add.Items.Add(Item(algorithm.DisplayName, () => Session.AddNode(algorithm.TypeId, NodeCanvas.ViewportLocation.X + 80, NodeCanvas.ViewportLocation.Y + 80)));
            menu.Items.Add(add); menu.Items.Add(Shared("粘贴", "Paste", "Ctrl+V")); menu.Items.Add(Shared("全选", "SelectAll", "Ctrl+A"));
            menu.Items.Add(Shared("适应流程", "Fit")); menu.Items.Add(Shared("整理布局", "Layout"));
        }
        menu.IsOpen = true;
    }
    private async void RequestRun(EditorRunMode mode)
    {
        if (RunRequested is { } requested) requested(this, mode);
        else if (Session is not null) try { await Session.RunAsync(mode); } catch (Exception ex) { State.ShowMessage(ex.Message); }
    }
    private void InspectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        _inspectionPort = null; _inspectionNode = null;
        if (NodeCanvas.IsMouseOver && !IsTextEditingFocus(e.OriginalSource as DependencyObject)) NodeCanvas.Focus();
        if (e.ChangedButton != MouseButton.Left) return;
        _inspectionStart = e.GetPosition(this);
        _inspectionPort = FindDataContext<GraphPortViewModel>(e.OriginalSource as DependencyObject);
        if (_inspectionPort?.Port.IsInput == true) _inspectionPort = null;
        _inspectionNode = FindDataContext<GraphNodeViewModel>(e.OriginalSource as DependencyObject);
    }
    private void InspectionMouseUp(object sender, MouseButtonEventArgs e)
    {
        var port = _inspectionPort; var node = _inspectionNode; _inspectionPort = null; _inspectionNode = null;
        if (_disposed || e.ChangedButton != MouseButton.Left || !IsInspectionClick(_inspectionStart, e.GetPosition(this))) return;
        var session = Session;
        // Let Nodify finish/cancel its connection gesture first. No event is marked handled.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed || session is null || Session != session) return;
            if (port is not null && session.Nodes.Contains(port.Port.Node)) InspectOutput(port.Port.Node.NodeId, port.Port.Name);
            else if (node is not null && session.Nodes.Contains(node.Node))
            { session.SelectedNode = node.Node; NodeInspectionRequested?.Invoke(this, EventArgs.Empty); }
        }));
    }
    public static bool IsInspectionClick(Point start, Point end) => Math.Abs(end.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(end.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance;
    public bool InspectOutput(string nodeId, string portName)
    {
        var success = Session?.InspectOutput(nodeId, portName) == true;
        OutputInspectionRequested?.Invoke(this, EventArgs.Empty); return success;
    }
    private static T? FindDataContext<T>(DependencyObject? origin) where T : class
    {
        while (origin is not null)
        {
            if (origin is FrameworkElement element && element.DataContext is T value) return value;
            origin = origin is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(origin) : LogicalTreeHelper.GetParent(origin);
        }
        return null;
    }
    public void Dispose() { _disposed = true; if (ActiveContextMenu is { } menu) menu.IsOpen = false; ActiveContextMenu = null; _inspectionPort = null; _inspectionNode = null; State.Dispose(); }
}

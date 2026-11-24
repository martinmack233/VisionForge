using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Graph;
using VisionPlatform.Editor.Wpf.Parameters;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Editor.Wpf.Collections;
public sealed class SubflowEditorWindow : Window
{
    private readonly EditorSession _session;
    private readonly GraphEditorView _graph;
    private readonly ParameterPanel _parameters;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(12, 6, 12, 8) };
    private readonly string _resourceRoot;
    private readonly ListBox _problems = new() { MaxHeight = 150, DisplayMemberPath = "Label", Margin = new(12, 0, 12, 8), Visibility = Visibility.Collapsed };
    public SubflowDefinition? Result { get; private set; }
    public SubflowEditorWindow(NodeRegistry registry, string resourceRoot)
    {
        Title = "编辑逐项子流程"; Width = 1250; Height = 760; MinWidth = 800; MinHeight = 500;
        _resourceRoot = resourceRoot;
        var safe = new NodeRegistry();
        foreach (var id in registry.RegisteredTypeIds)
            if (Attribute.IsDefined(registry.Create(id).GetType(), typeof(SubflowSafeAttribute))) safe.Register(id, () => registry.Create(id));
        _session = new(safe); _graph = new() { Session = _session }; _parameters = new() { Session = _session };
        _graph.State.RestrictToolbox(safe.RegisteredTypeIds);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        SetResourceReference(BackgroundProperty, "StudioBackground"); SetResourceReference(ForegroundProperty, "StudioForeground");
        var root = new DockPanel(); var toolbar = new WrapPanel { Margin = new(10, 8, 10, 3) };
        AddButton(toolbar, "OCR 子流程模板", async () => await LoadBodyAsync(CollectionSubflows.Ocr()));
        AddButton(toolbar, "测量/判定模板", async () => await LoadBodyAsync(CollectionSubflows.Measure()));
        AddButton(toolbar, "应用子节点参数", () => { _parameters.ApplyChanges(); return Task.CompletedTask; });
        AddButton(toolbar, "保存子流程", () => { SaveBody(); return Task.CompletedTask; });
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var help = new TextBlock { Margin = new(12, 2, 12, 6), TextWrapping = TextWrapping.Wrap,
            Text = "① 当前项 Item：每次一个来源对象，接 ItemToRoi。② 索引 Index：从0开始，不是跨帧ID。③ 共享 Image：父节点必须明确连接同一原图。④ 收集输出：算法实际结果接 IterationOutput。\n业务未识别/不合格不等于执行异常。异常按父节点Stop/Continue策略处理；空集合为0项；取消或停止后的部分结果不能当作全部完成。仅顺序算法，禁止嵌套、相机和通信。完整示例：主界面“方案示例 → 集合与子流程”中的多区域OCR、Blob测量。" };
        DockPanel.SetDock(help, Dock.Top); root.Children.Add(help); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        DockPanel.SetDock(_problems, Dock.Bottom); root.Children.Add(_problems);
        _problems.SelectionChanged += (_, _) => { if (_problems.SelectedItem is VisionPlatform.Editor.Core.Parameters.SubflowProblem problem && _session.Nodes.FirstOrDefault(n => n.NodeId == problem.NodeId) is { } node) _graph.FocusNode(node); };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(7) }); grid.ColumnDefinitions.Add(new() { Width = new(300), MinWidth = 250 });
        grid.Children.Add(_graph); var splitter = new GridSplitter { Width = 7, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        Grid.SetColumn(_parameters, 2); grid.Children.Add(_parameters); root.Children.Add(grid); Content = root;
        _session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(_session.StatusMessage)) _status.Text = _session.StatusMessage; };
        _graph.RunRequested += (_, _) => _session.SetStatus("当前项由父流程提供；请保存后运行父流程，不使用上次迭代缓存作输入。");
        Closed += (_, _) => { _graph.Dispose(); _session.Dispose(); };
    }
    public async Task LoadBodyAsync(SubflowDefinition body)
    {
        await _session.LoadSubflowForEditingAsync(body, _resourceRoot); _graph.FitToContent();
    }
    private void SaveBody()
    {
        if (_parameters.HasUnappliedDraft && !_parameters.ApplyChanges()) return;
        var project = _session.SnapshotProject();
        var body = new SubflowDefinition { Nodes = project.Nodes, Connections = project.Connections,
            InputNodeId = project.Nodes.FirstOrDefault(n => n.NodeTypeId == "IterationInput")?.NodeId ?? "current",
            OutputNodeId = project.Nodes.FirstOrDefault(n => n.NodeTypeId == "IterationOutput")?.NodeId ?? "collect",
            NodeLocations = _session.Nodes.ToDictionary(n => n.NodeId, n => new Point2D(n.X, n.Y)) };
        var errors = VisionPlatform.Editor.Core.Parameters.SubflowProblems.Find(_session.Registry, body);
        _problems.ItemsSource = errors; _problems.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (errors.Count > 0) { _status.Text = "子流程未保存：单击问题定位节点和端口。"; _problems.SelectedIndex = 0; return; }
        Result = body; DialogResult = true;
    }
    private void AddButton(Panel panel, string text, Func<Task> action)
    {
        var b = new Button { Content = text, Margin = new(3), Padding = new(10, 6, 10, 6) };
        b.Click += async (_, _) => { try { await action(); } catch (Exception ex) { _status.Text = ex.Message; } }; panel.Children.Add(b);
    }
}

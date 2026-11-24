using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Runtime.Execution;

namespace VisionPlatform.Editor.Wpf.Collections;
public sealed record CollectionTableRow(int Index, VisionItem Item, string State, IReadOnlyDictionary<string, string> Values)
{ public string Source => Item.Origin.Key; }
public sealed class CollectionResultsView : UserControl
{
    public static readonly DependencyProperty SessionProperty = DependencyProperty.Register(nameof(Session), typeof(IEditorSession), typeof(CollectionResultsView), new PropertyMetadata(null, Changed));
    public IEditorSession? Session { get => (IEditorSession?)GetValue(SessionProperty); set => SetValue(SessionProperty, value); }
    public event Action<RotatedRectRoi>? FocusRequested;
    private readonly DataGrid _table = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, EnableRowVirtualization = true,
        EnableColumnVirtualization = true, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 90 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) };
    private readonly TextBox _details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 70, MaxHeight = 150 };
    private readonly ComboBox _iteration = new() { MinWidth = 100, Margin = new(0, 3, 0, 3), DisplayMemberPath = "Index" };
    private readonly ComboBox _node = new() { Margin = new(0, 3, 0, 3), DisplayMemberPath = "NodeId" };
    private readonly ComboBox _port = new() { Margin = new(0, 3, 0, 3) };
    private readonly Button _copy = new() { Content = "复制完整结果文本", Margin = new(0, 3, 6, 5) };
    private readonly Button _local = new() { Content = "查看所选局部", Margin = new(0, 3, 0, 5), IsEnabled = false };
    private CollectionDisplayResult? _report;
    private bool _updating;
    public DataGrid ResultTable => _table;
    public CollectionResultsView()
    {
        var grid = new Grid { Margin = new(12), MinHeight = 600 }; grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 90 }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(_summary); var actions = new WrapPanel(); actions.Children.Add(_copy); actions.Children.Add(_local); Grid.SetRow(actions, 1); grid.Children.Add(actions);
        Grid.SetRow(_table, 2); grid.Children.Add(_table); Grid.SetRow(_details, 3); grid.Children.Add(_details);
        var debug = new StackPanel { Margin = new(0, 8, 0, 0) }; debug.Children.Add(new TextBlock { Text = "逐项调试 · 有界快照", FontWeight = FontWeights.SemiBold });
        debug.Children.Add(_iteration); debug.Children.Add(_node); debug.Children.Add(_port);
        var inspect = new Button { Content = "查看迭代端口 / 中间图像", Margin = new(0, 4, 0, 3) }; debug.Children.Add(inspect);
        debug.Children.Add(new TextBlock { Text = "只列出本次保留的迭代。未保留的图像不会复用旧结果；可在逐项节点调整保留数量及预算。", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(debug, 4); grid.Children.Add(debug); _table.Height = 240; Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _copy.Click += (_, _) => { if (_report is not null) Clipboard.SetText(_report.CopyText()); };
        _local.Click += (_, _) => { if (_table.SelectedItem is CollectionTableRow { Item.Region: { } roi }) FocusRequested?.Invoke(roi); };
        _table.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            var row = _table.SelectedItem as CollectionTableRow; (Session as EditorSession)?.SelectCollectionItem(row?.Item); _local.IsEnabled = row?.Item.Region is not null;
            _details.Text = row is null ? "选择一项查看完整字段和来源。" : $"序号（从0开始）：{row.Index}\n来源：{row.Item.Origin.Key}\nFrameId：{row.Item.Origin.FrameId?.ToString() ?? "原结果未提供"}\n坐标空间：{row.Item.Origin.CoordinateSpaceId}\n" + string.Join("\n", row.Item.Fields.Select(p => p.Key + " = " + p.Value.Format(12)));
        };
        _iteration.SelectionChanged += (_, _) => { if (!_updating) { _node.ItemsSource = (_iteration.SelectedItem as IterationTrace)?.Nodes; _node.SelectedIndex = 0; } };
        _node.SelectionChanged += (_, _) => { _port.ItemsSource = (_node.SelectedItem as NodeExecutionResult)?.Outputs.Keys; _port.SelectedIndex = 0; };
        inspect.Click += (_, _) =>
        {
            if (Session is EditorSession session && _iteration.SelectedItem is IterationTrace trace && _node.SelectedItem is NodeExecutionResult node && _port.SelectedItem is string port)
            { session.InspectIterationOutput(trace.Index, node.NodeId, port); _details.Text = $"迭代 {trace.Index} · RunId {trace.RunId}\n{node.NodeId}：{node.Status} · {node.ExecutionTime.TotalMilliseconds:F3} ms\n{node.ErrorMessage}\n" + string.Join("\n", session.SelectedOutputInspection?.Fields.Select(f => f.Name + ": " + f.Value) ?? []); }
        };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CollectionResultsView)d; if (e.OldValue is IEditorSession old) old.PropertyChanged -= view.SessionChanged;
        if (e.NewValue is IEditorSession current) current.PropertyChanged += view.SessionChanged; view.Refresh();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is "CollectionDisplay" or "IterationTraces" or nameof(IEditorSession.Revision)) Refresh(); }
    public void Refresh()
    {
        _updating = true;
        try
        {
            _report = (Session as EditorSession)?.CollectionDisplay; _table.ItemsSource = null; _table.Columns.Clear();
            _summary.Text = _report is null ? "运行后选择集合输出端口；空集合与未执行分别显示。" : $"{_report.Collection.Kind} · 共{_report.Collection.Count}项 · 当前表格{_report.VisibleItems.Count()}项（绘制最多{_report.Options.MaximumOverlays}项，底层数据不丢弃）";
            _table.Columns.Add(new DataGridTextColumn { Header = "序号", Binding = new Binding("Index"), Width = 50 });
            _table.Columns.Add(new DataGridTextColumn { Header = "业务状态", Binding = new Binding("State"), Width = 90 });
            if (_report is { } report)
            {
                var available = report.Collection.Items.SelectMany(i => i.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var field in report.Options.Fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(f => available.Contains(f, StringComparer.OrdinalIgnoreCase))
                    .Concat(available).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !f.Equals("Index", StringComparison.OrdinalIgnoreCase) && !f.Equals("State", StringComparison.OrdinalIgnoreCase)).Take(128))
                    _table.Columns.Add(new DataGridTextColumn { Header = field, Binding = new Binding("Values[" + field + "]"), Width = 115 });
                _table.ItemsSource = report.VisibleItems.Select(p => new CollectionTableRow(p.Index, p.Item, p.Item.State.ToString(),
                    p.Item.Fields.ToDictionary(f => f.Key, f => f.Value.Format(report.Options.DecimalPlaces), StringComparer.OrdinalIgnoreCase))).ToArray();
            }
            _details.Text = _report?.Collection.Count == 0 ? "当前执行返回空集合（0项），没有沿用旧结果。" : "选择一项高亮目标；“查看所选局部”在原图中放大，不生成缩略图坐标。";
            _copy.IsEnabled = _report is not null; _local.IsEnabled = false;
            _iteration.ItemsSource = (Session as EditorSession)?.IterationTraces; _iteration.SelectedIndex = 0;
            _node.ItemsSource = (_iteration.SelectedItem as IterationTrace)?.Nodes; _node.SelectedIndex = 0;
        }
        finally { _updating = false; }
    }
}

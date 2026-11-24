using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Editor.Core.Parameters;

namespace VisionPlatform.Editor.Wpf.Parameters;

/// <summary>Finite numeric cells only. No expressions; detached until an explicit confirmation.</summary>
public sealed class MatrixTableDraft
{
    public int Rows { get; }
    public int Columns { get; }
    public string[][] Cells { get; }
    public MatrixTableDraft(int rows, int columns, string text)
    {
        if (rows is < 1 or > 32 || columns is < 1 or > 32) throw new ArgumentException("表格编辑支持1..32行、1..32列；更大矩阵请使用数值文本或连接矩阵输入，没有截断内容。");
        var lines = text.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > rows) throw new ArgumentException("现有文本的行数超过指定行数，请先核对；未截断现有数值。");
        Rows = rows; Columns = columns; Cells = new string[rows][];
        for (var row = 0; row < rows; row++)
        {
            var fields = row < lines.Length ? lines[row].Split([',', ' ', '\t', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
            if (fields.Length > columns) throw new ArgumentException($"第{row + 1}行的列数超过指定列数，请先核对；未截断数值。");
            Cells[row] = Enumerable.Range(0, columns).Select(column => column < fields.Length ? fields[column] : "").ToArray();
        }
    }
    public bool TryBuild(out string text, out string? error, out int badRow, out int badColumn)
    {
        var lines = new string[Rows];
        for (var row = 0; row < Rows; row++)
        {
            var fields = new string[Columns];
            for (var column = 0; column < Columns; column++)
            {
                if (!double.TryParse(Cells[row][column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                { text = ""; error = $"第{row + 1}行、第{column + 1}列（索引{row},{column}）需要有限数字；保留了你的输入，请修正。"; badRow = row; badColumn = column; return false; }
                fields[column] = value.ToString("G17", CultureInfo.InvariantCulture);
            }
            lines[row] = string.Join(", ", fields);
        }
        text = string.Join(Environment.NewLine, lines); error = null; badRow = badColumn = -1; return true;
    }
}

/// <summary>One reusable editor for MatrixCreate.ValuesText / TransformCreate.MatrixText.</summary>
public sealed class MatrixTableEditor : UserControl
{
    private readonly ParameterValue _field;
    private readonly ParameterDraft _draft;
    private readonly string _nodeTypeId;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) };
    public TextBox TextEditor { get; } = new() { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 60, MaxHeight = 150, Padding = new(7) };
    public Button OpenTableButton { get; } = new() { Content = "表格编辑…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 4), MinHeight = 32 };
    public MatrixTableEditor(ParameterValue field, ParameterDraft draft, string nodeTypeId)
    {
        _field = field; _draft = draft; _nodeTypeId = nodeTypeId;
        var panel = new StackPanel(); Content = panel;
        TextEditor.SetBinding(TextBox.TextProperty, new Binding(nameof(ParameterValue.Text)) { Source = field, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        panel.Children.Add(TextEditor); panel.Children.Add(OpenTableButton);
        panel.Children.Add(new TextBlock { Text = "逐行填写数字，以逗号或空格分列；表格最多32×32。只有确认表格后才更新草稿，仍须点击参数面板‘应用’。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 3) });
        _error.SetResourceReference(TextBlock.ForegroundProperty, "StudioFailure"); panel.Children.Add(_error);
        System.Windows.Automation.AutomationProperties.SetAutomationId(OpenTableButton, "Matrix.OpenTable." + field.Descriptor.Path);
        OpenTableButton.Click += (_, _) => OpenTable();
    }
    public MatrixTableDraft CreateTableDraft()
    {
        var rows = 0; var columns = 0;
        if (_nodeTypeId == "TransformCreate")
        {
            var kind = _draft.Fields.Single(f => f.Descriptor.Path == "Kind").Text;
            rows = kind == "Affine" ? 2 : kind == "Perspective" ? 3 : throw new ArgumentException("请先选择仿射或透视变换类型。"); columns = 3;
        }
        else if (_nodeTypeId == "MatrixCreate")
        {
            if (!int.TryParse(_draft.Fields.Single(f => f.Descriptor.Path == "Rows").Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out rows) ||
                !int.TryParse(_draft.Fields.Single(f => f.Descriptor.Path == "Columns").Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out columns))
                throw new ArgumentException("请先输入有效的矩阵行数和列数。");
        }
        else throw new ArgumentException("此编辑器只用于数学矩阵或几何矩阵系数。");
        return new(rows, columns, _field.Text);
    }
    public bool ApplyTableDraft(MatrixTableDraft table)
    {
        if (!table.TryBuild(out var text, out var error, out _, out _)) { _error.Text = error; return false; }
        _field.Text = text; _error.Text = "表格已写入参数草稿，尚未应用到节点。"; return true;
    }
    private void OpenTable()
    {
        try
        {
            var table = CreateTableDraft(); var dialog = new TableDialog(table); var owner = Window.GetWindow(this); if (owner is not null) dialog.Owner = owner;
            if (dialog.ShowDialog() == true) ApplyTableDraft(table);
        }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
    private sealed record TableRow(string[] Cells);
    private sealed class TableDialog : Window
    {
        private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, RowHeight = 32, HeadersVisibility = DataGridHeadersVisibility.All, Margin = new(0, 10, 0, 8) };
        public TableDialog(MatrixTableDraft draft)
        {
            Title = $"矩阵表格 · {draft.Rows}×{draft.Columns} · Float64"; Width = Math.Clamp(draft.Columns * 120 + 90, 440, 1080); Height = Math.Clamp(draft.Rows * 32 + 180, 290, 760); MinWidth = 400; MinHeight = 260; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "StudioBackground"); var panel = new DockPanel { Margin = new(16) }; Content = panel;
            var hint = new TextBlock { Text = "行列索引从0开始；每个单元格仅允许有限数字。取消/关闭不修改原参数，确认才以G17精度写入草稿。", TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
            var bottom = new StackPanel(); var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) }; error.SetResourceReference(TextBlock.ForegroundProperty, "StudioFailure"); bottom.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; var confirm = new Button { Content = "确认写入草稿", MinHeight = 32, Margin = new(0, 0, 8, 0) }; var cancel = new Button { Content = "取消", IsCancel = true, MinHeight = 32 }; buttons.Children.Add(confirm); buttons.Children.Add(cancel); bottom.Children.Add(buttons); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
            var rows = draft.Cells.Select(cells => new TableRow(cells)).ToArray(); _grid.ItemsSource = rows;
            for (var column = 0; column < draft.Columns; column++) _grid.Columns.Add(new DataGridTextColumn { Header = "列 " + column, Binding = new Binding($"Cells[{column}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120, MinWidth = 95 });
            _grid.LoadingRow += (_, e) => e.Row.Header = "行 " + e.Row.GetIndex(); panel.Children.Add(_grid);
            confirm.Click += (_, _) =>
            {
                _grid.CommitEdit(DataGridEditingUnit.Cell, true); _grid.CommitEdit(DataGridEditingUnit.Row, true);
                if (draft.TryBuild(out _, out var message, out var badRow, out var badColumn)) { DialogResult = true; return; }
                error.Text = message; _grid.ScrollIntoView(rows[badRow], _grid.Columns[badColumn]); _grid.CurrentCell = new(rows[badRow], _grid.Columns[badColumn]); _grid.Focus(); _grid.BeginEdit();
            };
        }
    }
}

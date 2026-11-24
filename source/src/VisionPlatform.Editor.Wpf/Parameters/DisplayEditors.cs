using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Parameters;

public sealed class DisplayFieldSelector : StackPanel
{
    private readonly ParameterValue _field;
    private readonly Func<IReadOnlyList<DisplayFieldChoice>> _available;
    private readonly StackPanel _rows = new();
    private bool _updating;
    public IReadOnlyList<string> AvailableKeys { get; private set; } = [];
    public DisplayFieldSelector(ParameterValue field, IEditorSession session, EditorNode node)
    {
        _field = field; _available = () => DisplayFieldCatalog.Available(session, node);
        Children.Add(new TextBlock { Text = "勾选显示内容；↑↓调整顺序。字段来自本次输入或已连接类型。", TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "更新可用字段", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 5, 0, 5) };
        refresh.Click += (_, _) => Refresh(); Children.Add(refresh); Children.Add(_rows);
        field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(field.Text) && !_updating) Refresh(); }; Refresh();
    }
    private List<string> Selected() => _field.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public void Refresh()
    {
        _rows.Children.Clear(); var selected = Selected(); var available = _available(); AvailableKeys = available.Select(f => f.Key).ToArray();
        var visibleSelected = selected.Where(k => AvailableKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
        var ordered = available.OrderBy(f => { var i = selected.FindIndex(k => k.Equals(f.Key, StringComparison.OrdinalIgnoreCase)); return i < 0 ? int.MaxValue : i; });
        foreach (var choice in ordered)
        {
            var row = new DockPanel { Margin = new(0, 3, 0, 3) };
            var order = Array.FindIndex(visibleSelected, k => k.Equals(choice.Key, StringComparison.OrdinalIgnoreCase));
            var down = new Button { Content = "↓", Padding = new(6, 1, 6, 1), Margin = new(2, 0, 0, 0), ToolTip = "向后显示", IsEnabled = order >= 0 && order < visibleSelected.Length - 1 };
            var up = new Button { Content = "↑", Padding = new(6, 1, 6, 1), Margin = new(2, 0, 0, 0), ToolTip = "向前显示", IsEnabled = order > 0 };
            DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right); row.Children.Add(down); row.Children.Add(up);
            var check = new CheckBox { Content = choice.Label, IsChecked = order >= 0, ToolTip = "字段键：" + choice.Key, VerticalAlignment = VerticalAlignment.Center };
            check.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "DisplayField." + choice.Key);
            check.Click += (_, _) => { var keys = Selected(); keys.RemoveAll(k => k.Equals(choice.Key, StringComparison.OrdinalIgnoreCase)); if (check.IsChecked == true) keys.Add(choice.Key); Write(keys); };
            void Move(int delta) { var original = Selected(); var keys = original.Where(k => AvailableKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(); var index = keys.FindIndex(k => k.Equals(choice.Key, StringComparison.OrdinalIgnoreCase)); if (index < 0 || index + delta < 0 || index + delta >= keys.Count) return; (keys[index], keys[index + delta]) = (keys[index + delta], keys[index]); keys.AddRange(original.Where(k => !AvailableKeys.Contains(k, StringComparer.OrdinalIgnoreCase))); Write(keys); }
            up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1); row.Children.Add(check); _rows.Children.Add(row);
        }
        var unavailable = selected.Where(k => !AvailableKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unavailable.Length > 0) _rows.Children.Add(new TextBlock { Text = "旧配置保留的暂不可用字段：" + string.Join("、", unavailable) + "。不会伪造值；连接对应输入或运行后可更新字段列表。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0), FontSize = 11 });
    }
    private void Write(List<string> keys) { _updating = true; try { _field.Text = string.Join(',', keys); } finally { _updating = false; } Refresh(); }
}

public sealed class ColorParameterEditor : DockPanel
{
    private readonly Border _preview = new() { Width = 25, Height = 25, Margin = new(0, 0, 6, 0), BorderThickness = new(1), BorderBrush = Brushes.Gray };
    public static bool TryColor(string value, out Color color)
    { color = default; if (value.Length != 7 || value[0] != '#' || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false; color = Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n); return true; }
    public ColorParameterEditor(ParameterValue field)
    {
        Children.Add(_preview); var choose = new Button { Content = "选色", Padding = new(7, 4, 7, 4), Margin = new(5, 0, 0, 0) }; SetDock(choose, Dock.Right); Children.Add(choose);
        var text = new TextBox { MinWidth = 80, ToolTip = "支持手动输入 #RRGGBB；透明度使用独立参数。" };
        text.SetBinding(TextBox.TextProperty, new Binding(nameof(field.Text)) { Source = field, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); Children.Add(text);
        void Update() { var valid = TryColor(field.Text, out var color); _preview.Background = valid ? new SolidColorBrush(color) : Brushes.Transparent; _preview.ToolTip = valid ? field.Text : "颜色无效，请输入 #RRGGBB"; }
        field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(field.Text)) Update(); }; Update();
        choose.Click += (_, _) => { var picker = new ColorPickerWindow(field.Text) { Owner = Window.GetWindow(this) }; if (picker.ShowDialog() == true) field.Text = picker.SelectedHex; };
    }
}
public sealed class ColorPickerWindow : Window
{
    public string SelectedHex { get; private set; }
    public ColorPickerWindow(string initial)
    {
        Title = "选择颜色"; Width = 390; Height = 370; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "StudioBackground"); SetResourceReference(ForegroundProperty, "StudioForeground");
        SelectedHex = initial; var root = new StackPanel { Margin = new(16) }; Content = root;
        var preview = new Border { Height = 45, Margin = new(0, 0, 0, 8), BorderThickness = new(1) }; preview.SetResourceReference(Border.BorderBrushProperty, "StudioBorder"); root.Children.Add(preview);
        var entry = new TextBox { Text = initial, ToolTip = "#RRGGBB", Margin = new(0, 5, 0, 8) }; var palette = new WrapPanel(); root.Children.Add(palette); root.Children.Add(entry);
        foreach (var hex in new[] { "#243B53", "#2C7BE5", "#238B68", "#D94A53", "#D99A21", "#A62CA6", "#FFFFFF", "#000000", "#B9D8FF", "#C8DCF5" })
        { Try(hex, out var color); var swatch = new Button { Background = new SolidColorBrush(color), Width = 29, Height = 26, Margin = new(2), ToolTip = hex }; swatch.Click += (_, _) => entry.Text = hex; palette.Children.Add(swatch); }
        var sliders = new[] { new Slider(), new Slider(), new Slider() }; var changing = false;
        for (var i = 0; i < 3; i++) { var row = new DockPanel(); row.Children.Add(new TextBlock { Text = new[] { "红 R", "绿 G", "蓝 B" }[i], Width = 44 }); sliders[i].Minimum = 0; sliders[i].Maximum = 255; sliders[i].TickFrequency = 1; sliders[i].IsSnapToTickEnabled = true; sliders[i].Margin = new(0, 5, 0, 5); row.Children.Add(sliders[i]); root.Children.Add(row); }
        var message = new TextBlock { Margin = new(0, 5, 0, 5) }; root.Children.Add(message); var apply = new Button { Content = "使用此颜色", Margin = new(0, 8, 0, 0), MinHeight = 32 }; root.Children.Add(apply);
        void Refresh() { var valid = Try(entry.Text, out var color); apply.IsEnabled = valid; message.Text = valid ? "仅修改参数草稿，应用参数后生效。" : "请输入有效的 #RRGGBB 色值。"; if (!valid) return; preview.Background = new SolidColorBrush(color); changing = true; sliders[0].Value = color.R; sliders[1].Value = color.G; sliders[2].Value = color.B; changing = false; }
        entry.TextChanged += (_, _) => Refresh(); foreach (var slider in sliders) slider.ValueChanged += (_, _) => { if (!changing) entry.Text = $"#{(int)sliders[0].Value:X2}{(int)sliders[1].Value:X2}{(int)sliders[2].Value:X2}"; };
        apply.Click += (_, _) => { SelectedHex = entry.Text.ToUpperInvariant(); DialogResult = true; }; Refresh();
        static bool Try(string hex, out Color color) => ColorParameterEditor.TryColor(hex, out color);
    }
}

public sealed class DisplayNumberEditor : StackPanel
{
    public DisplayNumberEditor(ParameterValue field)
    {
        var row = new DockPanel(); var plus = new Button { Content = "+", Padding = new(9, 3, 9, 3), Margin = new(4, 0, 0, 0) }; var minus = new Button { Content = "−", Padding = new(9, 3, 9, 3), Margin = new(4, 0, 0, 0) };
        DockPanel.SetDock(plus, Dock.Right); DockPanel.SetDock(minus, Dock.Right); row.Children.Add(plus); row.Children.Add(minus);
        var text = new TextBox(); text.SetBinding(TextBox.TextProperty, new Binding(nameof(field.Text)) { Source = field, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }); row.Children.Add(text); Children.Add(row);
        var min = field.Descriptor.Minimum ?? -10000; var max = field.Descriptor.Maximum ?? 10000; var step = max <= 1 ? .01 : field.Descriptor.Kind == ParameterKind.Integer ? 1 : .1;
        void Adjust(double delta) { if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) field.Text = Math.Clamp(value + delta, min, max).ToString("G17", CultureInfo.InvariantCulture); }
        plus.Click += (_, _) => Adjust(step); minus.Click += (_, _) => Adjust(-step);
        if (field.Descriptor.Path is "BackgroundOpacity" or "FillOpacity" or "FontSize" or "LineWidth")
        {
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, SmallChange = step, Margin = new(0, 5, 0, 0) }; var updating = false;
            void Refresh() { updating = true; if (double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) slider.Value = Math.Clamp(value, min, max); updating = false; }
            slider.ValueChanged += (_, _) => { if (!updating) field.Text = slider.Value.ToString("G17", CultureInfo.InvariantCulture); };
            field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(field.Text)) Refresh(); }; Refresh(); Children.Add(slider);
        }
    }
}
public sealed record EnumChoice(string Key, string Label, string Description);
public static class EnumPresentation
{
    public static IReadOnlyList<EnumChoice> Choices(ParameterFieldDescriptor field) => field.Choices.Select(key =>
    {
        var label = (field.ValueType.Name, key) switch
        {
            ("MessageBoundaryMode","Delimiter")=>"分隔符完整消息",("MessageBoundaryMode","LengthPrefix")=>"4字节大端长度前缀",
            ("MessageEncoding","Utf8")=>"严格UTF-8",("MessageEncoding","Ascii")=>"严格ASCII",("MessageEncoding","Utf16LittleEndian")=>"UTF-16小端",
            ("Parity","None")=>"无校验",("Parity","Even")=>"偶校验",("Parity","Odd")=>"奇校验",("Parity","Mark")=>"标记校验",("Parity","Space")=>"空格校验",
            ("StopBits","One")=>"1停止位",("StopBits","Two")=>"2停止位",("StopBits","OnePointFive")=>"1.5停止位",("StopBits","None")=>"无停止位（不可用）",
            ("BusinessState", "Ok") => "合格 OK", ("BusinessState", "Ng") => "不合格 NG", ("BusinessState", "Unrecognized") => "未识别", ("BusinessState", "Error") => "执行异常", ("BusinessState", "Unfinished") => "未完成",
            ("ItemFailurePolicy", "Stop") => "执行异常时停止", ("ItemFailurePolicy", "Continue") => "记录异常后继续",
            ("OcrSegmentation", "SingleLine") => "单行文本", ("OcrSegmentation", "SingleBlock") => "多行文本块", ("OcrSegmentation", "Auto") => "自动布局",
            ("CollectionLabelPosition", "FollowTarget") => "跟随目标", ("CollectionLabelPosition", "Above") => "目标上方", ("CollectionLabelPosition", "Below") => "目标下方",
            ("CollectionLabelPosition", "TopLeft") => "视口左上角", ("CollectionLabelPosition", "TopRight") => "视口右上角", ("CollectionLabelPosition", "BottomLeft") => "视口左下角", ("CollectionLabelPosition", "BottomRight") => "视口右下角",
            ("WorkingColorSpace", "Hsv8") => "HSV（H:0..179；S/V:0..255）", ("WorkingColorSpace", "Lab8") => "Lab（L:0..255；a/b零点128）",
            ("ImageArithmeticOperation", "AbsoluteDifference") => "绝对差分", ("ImageArithmeticOperation", "Add") => "饱和加法", ("ImageArithmeticOperation", "Subtract") => "饱和减法", ("ImageArithmeticOperation", "Weighted") => "加权组合（8位饱和）",
            ("ContourRetrieval", "External") => "仅外边界", ("ContourRetrieval", "List") => "全部边界（无层级）", ("ContourRetrieval", "CComp") => "外边界/孔洞两层", ("ContourRetrieval", "Tree") => "完整轮廓树",
            ("ContourChain", "Simple") => "压缩直线段端点", ("ContourChain", "None") => "保留边界点",
            ("YoloExecutionMode", "Resident") => "会话常驻（复用模型）", ("YoloExecutionMode", "PerRequest") => "逐次进程（兼容对照）",
            ("YoloImageChannel", "Png") => "受控PNG文件", ("YoloImageChannel", "UncompressedTiff") => "受控无压缩TIFF文件",
            ("YoloQueuePolicy", "Wait") => "有界等待（每帧检测）", ("YoloQueuePolicy", "Reject") => "队列满明确拒绝",
            ("CollectionSliceMode", "All") => "选择全部", ("CollectionSliceMode", "FirstN") => "前N项", ("CollectionSliceMode", "Range") => "指定范围（从0开始）", _ => key
        };
        var declared=field.ValueType.GetField(key)?.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.DisplayAttribute),false).OfType<System.ComponentModel.DataAnnotations.DisplayAttribute>().FirstOrDefault();
        label=declared?.GetName()??label;
        return new EnumChoice(key,label,declared?.GetDescription()??(label==key?key:$"{label}；保存字段值：{key}"));
    }).ToArray();
}

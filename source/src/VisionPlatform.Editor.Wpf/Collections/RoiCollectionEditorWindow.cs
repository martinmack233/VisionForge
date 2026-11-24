using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Editor.Wpf.Collections;
public sealed class RoiCollectionEditorWindow : Window
{
    public sealed class Row
    {
        public string X { get; set; } = "100"; public string Y { get; set; } = "100";
        public string Width { get; set; } = "100"; public string Height { get; set; } = "40";
        public string Angle { get; set; } = "0"; public string Space { get; set; } = "image";
        public RotatedRectRoi Build()
        {
            double Parse(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : throw new ArgumentException("ROI坐标须为有限数值；输入已保留。");
            var roi = new RotatedRectRoi(Parse(X), Parse(Y), Parse(Width), Parse(Height), Parse(Angle), Space); roi.Validate(); return roi;
        }
        public static Row From(RotatedRectRoi r) => new() { X = r.CenterX.ToString("G17", CultureInfo.InvariantCulture), Y = r.CenterY.ToString("G17", CultureInfo.InvariantCulture),
            Width = r.Width.ToString("G17", CultureInfo.InvariantCulture), Height = r.Height.ToString("G17", CultureInfo.InvariantCulture), Angle = r.AngleDegrees.ToString("G17", CultureInfo.InvariantCulture), Space = r.CoordinateSpaceId };
    }
    public List<RotatedRectRoi>? Result { get; private set; }
    public RoiCollectionEditorWindow(IEnumerable<RotatedRectRoi> rois, RotatedRectRoi? current)
    {
        Title = "ROI集合 · 原图像素坐标"; Width = 820; Height = 500; MinWidth = 650; MinHeight = 350;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        var rows = new ObservableCollection<Row>(rois.Select(Row.From));
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Margin = new(12), EnableRowVirtualization = true };
        foreach (var (field, label) in new[] { ("X", "中心X"), ("Y", "中心Y"), ("Width", "宽"), ("Height", "高"), ("Angle", "顺时针角度°"), ("Space", "坐标空间") })
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(field) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new(1, DataGridLengthUnitType.Star) });
        var root = new DockPanel(); var bar = new WrapPanel { Margin = new(12, 8, 12, 0) }; var error = new TextBlock { Margin = new(12), TextWrapping = TextWrapping.Wrap };
        void Button(string label, Action action) { var b = new Button { Content = label, Margin = new(3), Padding = new(10, 6, 10, 6) }; b.Click += (_, _) => { try { grid.CommitEdit(); action(); } catch (Exception ex) { error.Text = ex.Message; } }; bar.Children.Add(b); }
        Button("添加", () => rows.Add(new())); Button("加入当前绘制ROI", () => { if (current is null) throw new ArgumentException("请先在原图绘制ROI。"); rows.Add(Row.From(current)); });
        Button("删除所选", () => { if (grid.SelectedItem is Row row) rows.Remove(row); });
        Button("保存集合", () => { Result = rows.Select(r => r.Build()).ToList(); if (Result.Count > 10000) throw new ArgumentException("最多10000项。"); DialogResult = true; });
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error); root.Children.Add(grid); Content = root;
    }
}

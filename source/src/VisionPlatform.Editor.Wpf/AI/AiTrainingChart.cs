using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VisionPlatform.Editor.Wpf.AI;

/// <summary>Draws only metrics returned by the existing training service. Missing epochs are never synthesized.</summary>
public sealed class AiTrainingChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(ObservableCollection<AiMetricPoint>), typeof(AiTrainingChart), new FrameworkPropertyMetadata(null, (d, e) =>
    { var chart = (AiTrainingChart)d; if (e.OldValue is ObservableCollection<AiMetricPoint> old) old.CollectionChanged -= chart.Changed; if (e.NewValue is ObservableCollection<AiMetricPoint> next) next.CollectionChanged += chart.Changed; chart.InvalidateVisual(); }));
    public ObservableCollection<AiMetricPoint>? Points { get => (ObservableCollection<AiMetricPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var foreground = TryFindResource("StudioForeground") as Brush ?? Brushes.SlateGray;
        var grid = TryFindResource("StudioBorder") as Brush ?? Brushes.LightBlue;
        var area = new Rect(38, 16, Math.Max(1, ActualWidth - 56), Math.Max(1, ActualHeight - 48));
        dc.DrawRectangle(null, new Pen(grid, 1), area);
        var points = Points?.ToArray() ?? [];
        if (points.Length == 0) { Text(dc, "暂无实际 Epoch 指标", new(50, 30), foreground); return; }
        var maxEpoch = Math.Max(1, points.Max(p => p.Epoch));
        var values = points.SelectMany(p => new[] { p.BoxLoss, p.ClassLoss, p.Map50, p.Map }).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var max = values.Length > 0 ? Math.Max(1, values.Max()) : 1;
        for (var i = 1; i < 4; i++) { var y = area.Bottom - area.Height * i / 4; dc.DrawLine(new Pen(grid, .5), new(area.Left, y), new(area.Right, y)); }
        Series(p => p.BoxLoss, Brushes.OrangeRed); Series(p => p.ClassLoss, Brushes.DarkOrange); Series(p => p.Map50, Brushes.SeaGreen); Series(p => p.Map, Brushes.RoyalBlue);
        Text(dc, $"Epoch 0 → {maxEpoch:G}    红：box loss  橙：cls loss  绿：mAP50  蓝：mAP50-95", new(38, area.Bottom + 8), foreground);
        void Series(Func<AiMetricPoint, double?> selector, Brush brush)
        {
            Point? previous = null;
            foreach (var p in points)
            {
                if (selector(p) is not { } value) { previous = null; continue; }
                var current = new Point(area.Left + p.Epoch / maxEpoch * area.Width, area.Bottom - value / max * area.Height);
                if (previous is { } prior) dc.DrawLine(new Pen(brush, 1.8), prior, current); dc.DrawEllipse(brush, null, current, 2, 2); previous = current;
            }
        }
    }
    private void Text(DrawingContext dc, string value, Point point, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;

namespace VisionPlatform.Editor.Wpf.Imaging;
public sealed partial class ImageViewportControl
{
    public static readonly DependencyProperty CollectionDisplayProperty = DependencyProperty.Register(nameof(CollectionDisplay), typeof(CollectionDisplayResult), typeof(ImageViewportControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((ImageViewportControl)d)._canvas.InvalidateVisual()));
    public static readonly DependencyProperty CollectionSelectionProperty = DependencyProperty.Register(nameof(CollectionSelection), typeof(VisionItem), typeof(ImageViewportControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((ImageViewportControl)d)._canvas.InvalidateVisual()));
    public CollectionDisplayResult? CollectionDisplay { get => (CollectionDisplayResult?)GetValue(CollectionDisplayProperty); set => SetValue(CollectionDisplayProperty, value); }
    public VisionItem? CollectionSelection { get => (VisionItem?)GetValue(CollectionSelectionProperty); set => SetValue(CollectionSelectionProperty, value); }
    public void FocusRegion(RotatedRectRoi roi)
    {
        if (_frame is null || _frame.CoordinateSpace.Id != roi.CoordinateSpaceId) return;
        roi.Validate(); _fitMode = false;
        var corners = roi.Corners(); var width = corners.Max(p => p.X) - corners.Min(p => p.X); var height = corners.Max(p => p.Y) - corners.Min(p => p.Y);
        var zoom = Math.Clamp(Math.Min(Math.Max(1, _canvas.ActualWidth - 50) / width, Math.Max(1, _canvas.ActualHeight - 50) / height), .0001, 64);
        ViewTransform.ZoomAt(new(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2), zoom / ViewTransform.Zoom);
        var center = ViewTransform.ToScreen(new(roi.CenterX, roi.CenterY)); ViewTransform.Pan(_canvas.ActualWidth / 2 - center.X, _canvas.ActualHeight / 2 - center.Y);
        RequestVisibleTiles(); UpdateDescription(); _canvas.InvalidateVisual();
    }
    private void DrawCollections(DrawingContext drawing, Size viewport)
    {
        if (_frame is null || CollectionDisplay is not { } display) return;
        var p = display.Options; var fixedLine = 0;
        foreach (var (index, item) in display.VisibleItems.Take(p.MaximumOverlays))
        {
            if (!Matches(item)) continue;
            var color = Brush(p.Color(item)); var pen = new Pen(color, p.LineWidth); var fill = Brush(p.FillColor, p.FillOpacity);
            if (item is ContourItem actual) DrawShape(actual.Points, actual.Closed, pen, actual.Closed ? fill : null);
            else if (item.Region is { } roi) DrawShape(roi.Corners(), true, pen, fill);
            else if (CircleOf(item) is { } circle) DrawCircle(circle, pen, fill);
            else switch (item)
            {
                case CodeItem code: DrawShape(code.Points, true, pen, fill); break;
                case PointSetItem points: foreach (var point in points.Points.Take(2000)) drawing.DrawEllipse(null, pen, ViewTransform.ToScreen(point), 2, 2); break;
                case LineItem line: var o = line.Line.Origin; var v = line.Line.Direction; drawing.DrawLine(pen, ViewTransform.ToScreen(o), ViewTransform.ToScreen(line.EndPoint ?? new(o.X + v.X * 100, o.Y + v.Y * 100))); break;
            }
            var label = display.Label(item, index); if (label.Length > 512) label = label[..512] + "…";
            if (label.Length == 0) continue;
            var formatted = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), p.FontSize, Brush(p.TextColor), VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(10, Math.Min(600, viewport.Width - 24)), MaxLineCount = 3, Trimming = TextTrimming.CharacterEllipsis };
            Point anchor;
            if (p.Position is CollectionLabelPosition.TopLeft or CollectionLabelPosition.TopRight or CollectionLabelPosition.BottomLeft or CollectionLabelPosition.BottomRight)
            {
                var right = p.Position is CollectionLabelPosition.TopRight or CollectionLabelPosition.BottomRight;
                var bottom = p.Position is CollectionLabelPosition.BottomLeft or CollectionLabelPosition.BottomRight;
                anchor = new(right ? viewport.Width - formatted.Width - 12 : 12, bottom ? viewport.Height - (fixedLine + 1) * (formatted.Height + 4) - 12 : 12 + fixedLine * (formatted.Height + 4)); fixedLine++;
            }
            else if (item.Region is { } r)
            {
                var points = r.Corners().Select(ViewTransform.ToScreen).ToArray();
                anchor = p.Position == CollectionLabelPosition.FollowTarget ? ViewTransform.ToScreen(new(r.CenterX, r.CenterY)) : new(points.Min(v => v.X), p.Position == CollectionLabelPosition.Below ? points.Max(v => v.Y) + 4 : points.Min(v => v.Y) - formatted.Height - 4);
            }
            else if (CircleOf(item) is { } labelCircle)
            {
                anchor = ViewTransform.ToScreen(labelCircle.Center);
                var radius = labelCircle.Radius * ViewTransform.Zoom;
                if (p.Position == CollectionLabelPosition.Above) anchor.Y -= radius + formatted.Height + 4;
                if (p.Position == CollectionLabelPosition.Below) anchor.Y += radius + 4;
            }
            else
            {
                Point2D? position = item switch { LineItem l => l.Line.Origin, ContourItem c when c.Points.Count > 0 => c.Points[0], _ => null };
                if (position is null) continue;
                anchor = ViewTransform.ToScreen(position.Value);
            }
            anchor.Offset(p.OffsetX, p.OffsetY);
            anchor.X = Math.Clamp(anchor.X, 0, Math.Max(0, viewport.Width - formatted.Width - 6));
            anchor.Y = Math.Clamp(anchor.Y, 0, Math.Max(0, viewport.Height - formatted.Height - 3));
            drawing.DrawRectangle(Brush(p.BackgroundColor, p.BackgroundOpacity), null, new Rect(anchor, new Size(formatted.Width + 6, formatted.Height + 3))); drawing.DrawText(formatted, new(anchor.X + 3, anchor.Y + 1));
        }
        if (CollectionSelection is ContourItem selectedContour && Matches(selectedContour))
            DrawShape(selectedContour.Points, selectedContour.Closed, new Pen(Brush("#FFCB00"), Math.Max(3, p.LineWidth + 2)), null);
        else if (CollectionSelection is { Region: { } selected } itemSelected && Matches(itemSelected))
            DrawShape(selected.Corners(), true, new Pen(Brush("#FFCB00"), Math.Max(3, p.LineWidth + 2)), null);
        else if (CollectionSelection is { } selectedItem && Matches(selectedItem))
        {
            var highlight = new Pen(Brush("#FFCB00"), Math.Max(3, p.LineWidth + 2));
            if (CircleOf(selectedItem) is { } circle) DrawCircle(circle, highlight, null);
            else switch (selectedItem)
            {
                case CodeItem c: DrawShape(c.Points, true, highlight, null); break;
                case PointSetItem points: foreach (var point in points.Points.Take(2000)) drawing.DrawEllipse(null, highlight, ViewTransform.ToScreen(point), 4, 4); break;
                case LineItem l: drawing.DrawLine(highlight, ViewTransform.ToScreen(l.Line.Origin), ViewTransform.ToScreen(l.EndPoint ?? new(l.Line.Origin.X + l.Line.Direction.X * 100, l.Line.Origin.Y + l.Line.Direction.Y * 100))); break;
            }
        }
        bool Matches(VisionItem item) => item.Origin.CoordinateSpaceId == _frame.CoordinateSpace.Id && (item.Origin.FrameId is null || item.Origin.FrameId == _frame.FrameId);
        static CircleGeometry? CircleOf(VisionItem item) => item switch { CircleItem c => c.Circle, ContourEnvelopeItem c => c.Circle, _ => null };
        void DrawCircle(CircleGeometry circle, Pen pen, System.Windows.Media.Brush? fill)
        {
            var radius = circle.Radius * ViewTransform.Zoom;
            drawing.DrawEllipse(fill, pen, ViewTransform.ToScreen(circle.Center), radius, radius);
        }
        static SolidColorBrush Brush(string hex, double opacity = 1) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity }; brush.Freeze(); return brush; }
        void DrawShape(IReadOnlyList<Point2D> points, bool closed, Pen pen, System.Windows.Media.Brush? fill)
        {
            if (points.Count == 0) return; var geometry = new StreamGeometry();
            using (var path = geometry.Open()) { path.BeginFigure(ViewTransform.ToScreen(points[0]), fill is not null, closed); path.PolyLineTo(points.Skip(1).Take(10000).Select(ViewTransform.ToScreen).ToArray(), true, false); }
            geometry.Freeze(); drawing.DrawGeometry(fill, pen, geometry);
        }
    }
}

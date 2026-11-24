using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VisionPlatform.Editor.Wpf.AI;

/// <summary>AI annotations use original-image pixels. This independent editor never changes the inspection ROI model.</summary>
public sealed class AiAnnotationCanvas : FrameworkElement
{
    public static readonly DependencyProperty PreviewPathProperty = DependencyProperty.Register(nameof(PreviewPath), typeof(string), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata("", (d, e) => ((AiAnnotationCanvas)d).LoadPreview((string)e.NewValue)));
    public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(nameof(SourceWidth), typeof(int), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(nameof(SourceHeight), typeof(int), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BoxesProperty = DependencyProperty.Register(nameof(Boxes), typeof(ObservableCollection<AiBox>), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(null, (d, e) => ((AiAnnotationCanvas)d).AttachBoxes(e.OldValue as ObservableCollection<AiBox>, e.NewValue as ObservableCollection<AiBox>)));
    public static readonly DependencyProperty SelectedBoxProperty = DependencyProperty.Register(nameof(SelectedBox), typeof(AiBox), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty ClassesProperty = DependencyProperty.Register(nameof(Classes), typeof(ObservableCollection<AiClassItem>), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveClassProperty = DependencyProperty.Register(nameof(ActiveClass), typeof(AiClassItem), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(AiAnnotationCanvas), new FrameworkPropertyMetadata(false));
    public string PreviewPath { get => (string)GetValue(PreviewPathProperty); set => SetValue(PreviewPathProperty, value); }
    public int SourceWidth { get => (int)GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
    public int SourceHeight { get => (int)GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
    public ObservableCollection<AiBox>? Boxes { get => (ObservableCollection<AiBox>?)GetValue(BoxesProperty); set => SetValue(BoxesProperty, value); }
    public AiBox? SelectedBox { get => (AiBox?)GetValue(SelectedBoxProperty); set => SetValue(SelectedBoxProperty, value); }
    public ObservableCollection<AiClassItem>? Classes { get => (ObservableCollection<AiClassItem>?)GetValue(ClassesProperty); set => SetValue(ClassesProperty, value); }
    public AiClassItem? ActiveClass { get => (AiClassItem?)GetValue(ActiveClassProperty); set => SetValue(ActiveClassProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool DrawMode { get; set; }
    public string CoordinateText { get; private set; } = "原图像素坐标";
    public double Zoom => _scale;
    public Rect NativeTileRegion => _nativeRegion;
    public int NativeTilePixelWidth => _native?.PixelWidth ?? 0;
    public int NativeTilePixelHeight => _native?.PixelHeight ?? 0;
    public Point ToOriginal(Point viewportPoint) => ImagePoint(viewportPoint);
    public Point ToViewport(Point imagePoint) => ScreenPoint(imagePoint);
    public event Action? EditStarted;
    public event Action? Edited;
    public event Action? UndoRequested;
    public event Action? DeleteRequested;
    public event Action<int>? ClassRequested;
    public event Action<int>? ImageNavigationRequested;
    public event Action<Rect>? NativeRegionRequested;
    public event Action<string>? CoordinateChanged;
    public event Action<string>? PreviewError;
    private BitmapSource? _preview, _native;
    private Rect _nativeRegion;
    private double _scale = 1;
    private Vector _offset;
    private bool _fit = true;
    private long _generation;
    private Point _startScreen, _startImage, _prior;
    private AiBox? _original, _working;
    private int _corner;
    private bool _categorySequence;
    private string _categoryBuffer = "";
    private Mode _mode;
    private enum Mode { None, Pan, Draw, Move, Resize }
    private readonly DispatcherTimer _tileDebounce;
    public AiAnnotationCanvas()
    {
        Focusable = true; ClipToBounds = true; MinHeight = 130; Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _tileDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _tileDebounce?.Stop(); RequestVisiblePixels(); }, Dispatcher);
        _tileDebounce.Stop();
        SizeChanged += (_, _) => { if (_fit) Fit(); else QueueNative(); };
        Unloaded += (_, _) => { _tileDebounce.Stop(); _generation++; _native = null; };
    }
    private async void LoadPreview(string path)
    {
        var generation = ++_generation; _native = null; _preview = null; InvalidateVisual();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var bitmap = await Task.Run(() => ReadBitmap(path));
            if (generation != _generation) return;
            _preview = bitmap; _fit = true; Fit();
        }
        catch (Exception e) { if (generation == _generation) PreviewError?.Invoke(e.GetBaseException().Message); }
    }
    private static BitmapSource ReadBitmap(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public async Task SetNativeTileAsync(JsonElement result)
    {
        var generation = _generation; var path = AiJson.Text(result, "path");
        if (path.Length == 0) return;
        var bitmap = await Task.Run(() => ReadBitmap(path));
        if (generation != _generation) return;
        var region = AiJson.Field(result, "region");
        _native = bitmap; _nativeRegion = region.ValueKind == JsonValueKind.Object
            ? new(AiJson.Number(region, "x"), AiJson.Number(region, "y"), AiJson.Number(region, "width"), AiJson.Number(region, "height"))
            : new(AiJson.Number(result, "x"), AiJson.Number(result, "y"), bitmap.PixelWidth, bitmap.PixelHeight);
        InvalidateVisual();
    }
    private void AttachBoxes(ObservableCollection<AiBox>? old, ObservableCollection<AiBox>? next)
    {
        if (old is not null) { old.CollectionChanged -= BoxesChanged; foreach (var box in old) box.PropertyChanged -= BoxChanged; }
        if (next is not null) { next.CollectionChanged += BoxesChanged; foreach (var box in next) box.PropertyChanged += BoxChanged; }
        InvalidateVisual();
    }
    private void BoxesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (AiBox box in e.OldItems) box.PropertyChanged -= BoxChanged;
        if (e.NewItems is not null) foreach (AiBox box in e.NewItems) box.PropertyChanged += BoxChanged;
        InvalidateVisual();
    }
    private void BoxChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
    private int W => SourceWidth > 0 ? SourceWidth : _preview?.PixelWidth ?? 0;
    private int H => SourceHeight > 0 ? SourceHeight : _preview?.PixelHeight ?? 0;
    public void Fit()
    {
        if (W <= 0 || H <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        _fit = true; _scale = Math.Min(Math.Max(1, ActualWidth - 16) / W, Math.Max(1, ActualHeight - 16) / H);
        _offset = new((ActualWidth - W * _scale) / 2, (ActualHeight - H * _scale) / 2); _native = null; InvalidateVisual(); QueueNative();
    }
    public void OneToOne()
    { if (W <= 0 || H <= 0) return; _fit = false; _scale = 1; _offset = new((ActualWidth - W) / 2, (ActualHeight - H) / 2); InvalidateVisual(); QueueNative(); }
    private Point ImagePoint(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);
    private Point ScreenPoint(Point image) => new(image.X * _scale + _offset.X, image.Y * _scale + _offset.Y);
    private Rect ScreenRect(AiBox box) => new(ScreenPoint(new(box.X, box.Y)), new Size(box.Width * _scale, box.Height * _scale));
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing); drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 34, 42)), null, new Rect(RenderSize));
        if (_preview is null || W <= 0 || H <= 0) { DrawText(drawing, "选择图片以开始标注或查看结果", new(18, 18), Brushes.LightGray); return; }
        drawing.DrawImage(_preview, new Rect(_offset.X, _offset.Y, W * _scale, H * _scale));
        if (_native is not null) drawing.DrawImage(_native, new Rect(ScreenPoint(_nativeRegion.TopLeft), new Size(_nativeRegion.Width * _scale, _nativeRegion.Height * _scale)));
        foreach (var box in Boxes ?? []) DrawBox(drawing, box, ReferenceEquals(box, SelectedBox));
        if (_working is not null && _mode == Mode.Draw) DrawBox(drawing, _working, true);
    }
    private void DrawBox(DrawingContext drawing, AiBox box, bool selected)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        var category = Classes?.FirstOrDefault(c => c.Id == box.ClassId); var color = Brushes.OrangeRed;
        try { if (!string.IsNullOrWhiteSpace(category?.Color)) color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category.Color)); } catch (FormatException) { }
        var rect = ScreenRect(box); drawing.DrawRectangle(null, new Pen(color, selected ? 2.5 : 1.5), rect);
        DrawText(drawing, box.Label.Length > 0 ? box.Label : category?.Name ?? box.ClassId.ToString(CultureInfo.InvariantCulture), new(rect.X + 3, Math.Max(1, rect.Y - 19)), color);
        if (selected && !IsReadOnly) foreach (var p in Corners(rect)) drawing.DrawRectangle(Brushes.White, new Pen(color, 1), new Rect(p.X - 4, p.Y - 4, 8, 8));
    }
    private void DrawText(DrawingContext dc, string text, Point point, Brush brush) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI, Microsoft YaHei UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
    private static Point[] Corners(Rect rect) => [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_preview is null) return; var point = e.GetPosition(this); var origin = ImagePoint(point); _fit = false;
        _scale = Math.Clamp(_scale * Math.Pow(1.2, e.Delta / 120d), .002, 64); _offset = new(point.X - origin.X * _scale, point.Y - origin.Y * _scale); InvalidateVisual(); QueueNative(); e.Handled = true;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus(); if (_preview is null) return;
        _startScreen = _prior = e.GetPosition(this); _startImage = ImagePoint(_startScreen); _original = null; _working = null;
        if (e.ChangedButton != MouseButton.Left || Keyboard.IsKeyDown(Key.Space) || IsReadOnly) _mode = Mode.Pan;
        else if (DrawMode)
        {
            if (ActiveClass is null) { PreviewError?.Invoke("请先选择标注类别。"); return; }
            EditStarted?.Invoke(); _mode = Mode.Draw; _startImage = Clamp(_startImage);
            _working = new() { ClassId = ActiveClass.Id, X = _startImage.X, Y = _startImage.Y, Width = 1, Height = 1 };
        }
        else
        {
            var selected = SelectedBox;
            var handle = selected is null ? -1 : Array.FindIndex(Corners(ScreenRect(selected)), p => (p - _startScreen).Length <= 9);
            if (handle >= 0 && selected is not null) { _mode = Mode.Resize; _corner = handle; _original = selected.Copy(); EditStarted?.Invoke(); }
            else
            {
                selected = Boxes?.LastOrDefault(box => new Rect(box.X, box.Y, box.Width, box.Height).Contains(_startImage));
                SetCurrentValue(SelectedBoxProperty, selected);
                if (selected is null) _mode = Mode.Pan;
                else { _mode = Mode.Move; _original = selected.Copy(); EditStarted?.Invoke(); }
            }
        }
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this); var pixel = ImagePoint(screen); CoordinateText = $"X {pixel.X:F2} · Y {pixel.Y:F2} · {_scale * 100:F1}%"; CoordinateChanged?.Invoke(CoordinateText);
        if (!IsMouseCaptured || _mode == Mode.None) return;
        var current = Clamp(pixel);
        if (_mode == Mode.Pan) { _fit = false; _offset += screen - _prior; QueueNative(); }
        else if (_mode == Mode.Draw && _working is { } box)
        { box.X = Math.Min(_startImage.X, current.X); box.Y = Math.Min(_startImage.Y, current.Y); box.Width = Math.Max(1, Math.Abs(current.X - _startImage.X)); box.Height = Math.Max(1, Math.Abs(current.Y - _startImage.Y)); }
        else if (_mode == Mode.Move && SelectedBox is { } moving && _original is { } original)
        { moving.X = Math.Clamp(original.X + current.X - _startImage.X, 0, Math.Max(0, W - original.Width)); moving.Y = Math.Clamp(original.Y + current.Y - _startImage.Y, 0, Math.Max(0, H - original.Height)); }
        else if (_mode == Mode.Resize && SelectedBox is { } resizing && _original is { } before)
        {
            var opposite = Corners(new(before.X, before.Y, before.Width, before.Height))[(_corner + 2) % 4];
            resizing.X = Math.Min(opposite.X, current.X); resizing.Y = Math.Min(opposite.Y, current.Y); resizing.Width = Math.Max(1, Math.Abs(opposite.X - current.X)); resizing.Height = Math.Max(1, Math.Abs(opposite.Y - current.Y));
        }
        _prior = screen; InvalidateVisual();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return; var edit = _mode is Mode.Draw or Mode.Move or Mode.Resize;
        if (_mode == Mode.Draw && _working is { Width: >= 1, Height: >= 1 } box && (_startScreen - e.GetPosition(this)).Length >= 3)
        { Boxes?.Add(box); SetCurrentValue(SelectedBoxProperty, box); }
        else if (_mode == Mode.Draw) edit = false;
        _working = null; _mode = Mode.None; ReleaseMouseCapture(); if (edit) Edited?.Invoke(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsReadOnly) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var digit = key >= Key.D0 && key <= Key.D9 ? key - Key.D0 : key >= Key.NumPad0 && key <= Key.NumPad9 ? key - Key.NumPad0 : -1;
        if (_categorySequence)
        {
            if (key == Key.Escape) { _categorySequence = false; _categoryBuffer = ""; e.Handled = true; return; }
            if (digit >= 0) { if (_categoryBuffer.Length < 3) _categoryBuffer += digit.ToString(CultureInfo.InvariantCulture); CoordinateChanged?.Invoke("类别序号 " + _categoryBuffer + " · Enter确认 / Esc取消"); e.Handled = true; return; }
            if (key == Key.Enter || key == Key.Return)
            {
                if (int.TryParse(_categoryBuffer, out var number) && Classes is { } indexed && number > 0 && number <= indexed.Count) ClassRequested?.Invoke(indexed[number - 1].Id);
                _categorySequence = false; _categoryBuffer = ""; e.Handled = true; return;
            }
        }
        if (key == Key.K && modifiers == ModifierKeys.Control) { _categorySequence = true; _categoryBuffer = ""; CoordinateChanged?.Invoke("输入类别列表序号（1～999），Enter确认"); e.Handled = true; return; }
        if (key == Key.Escape) { CancelEdit(); e.Handled = true; return; }
        if (e.Key == Key.Delete) { DeleteRequested?.Invoke(); e.Handled = true; }
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { UndoRequested?.Invoke(); e.Handled = true; }
        if (digit >= 0)
        {
            var group = modifiers switch { ModifierKeys.Control => 1, ModifierKeys.Alt => 2, ModifierKeys.Control | ModifierKeys.Alt => 3, ModifierKeys.None => 0, _ => -1 };
            var index = group * 10 + (digit == 0 ? 9 : digit - 1);
            if (group >= 0 && Classes is { } classes && index < classes.Count) { ClassRequested?.Invoke(classes[index].Id); e.Handled = true; }
        }
        if (modifiers == ModifierKeys.None && key is Key.A or Key.D) { ImageNavigationRequested?.Invoke(key == Key.A ? -1 : 1); e.Handled = true; }
    }
    private void CancelEdit()
    {
        if (_mode is Mode.Move or Mode.Resize && SelectedBox is { } box && _original is { } before) { box.X = before.X; box.Y = before.Y; box.Width = before.Width; box.Height = before.Height; }
        _mode = Mode.None; _working = null; ReleaseMouseCapture(); InvalidateVisual();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (_mode != Mode.None) CancelEdit(); }
    private Point Clamp(Point point) => new(Math.Clamp(point.X, 0, Math.Max(0, W - 1)), Math.Clamp(point.Y, 0, Math.Max(0, H - 1)));
    private void QueueNative() { _tileDebounce.Stop(); _tileDebounce.Start(); }
    private void RequestVisiblePixels()
    {
        if (_scale < .25 || W <= 0 || H <= 0) { _native = null; return; }
        var a = Clamp(ImagePoint(new(0, 0))); var b = Clamp(ImagePoint(new(ActualWidth, ActualHeight)));
        var width = Math.Min(4096, Math.Max(1, b.X - a.X + 1)); var height = Math.Min(4096, Math.Max(1, b.Y - a.Y + 1));
        if ((b.X - a.X) > 4096 || (b.Y - a.Y) > 4096) return;
        NativeRegionRequested?.Invoke(new(a.X, a.Y, width, height));
    }
}

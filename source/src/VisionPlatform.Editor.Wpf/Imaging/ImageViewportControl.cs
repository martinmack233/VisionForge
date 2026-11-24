using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Wpf.ROI;
using VisionPlatform.Editor.Wpf.Themes;

namespace VisionPlatform.Editor.Wpf.Imaging;

public enum ImageInteractionMode { Select, Pan, Rectangle, RotatedRectangle }

/// <summary>Bounded asynchronous image preview with original-pixel ROI interaction. Only its own Share is disposed.</summary>
public sealed partial class ImageViewportControl : UserControl, IDisposable
{
    public static readonly DependencyProperty IsRoiEditingEnabledProperty = DependencyProperty.Register(nameof(IsRoiEditingEnabled), typeof(bool),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(true, (d, _) => ((ImageViewportControl)d).UpdateRoiTools()));
    public bool IsRoiEditingEnabled { get => (bool)GetValue(IsRoiEditingEnabledProperty); set => SetValue(IsRoiEditingEnabledProperty, value); }
    private readonly List<Button> _roiButtons = [];
    private void UpdateRoiTools()
    {
        foreach (var button in _roiButtons) button.Visibility = IsRoiEditingEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_coordinates is not null) _coordinates.Text = IsRoiEditingEnabled ? "滚轮缩放 · 中键拖动平移 · 选择 ROI 后拖动角点调整，圆点旋转" : "只读预览 · 滚轮缩放 · 拖动平移 · 检测坐标使用原始像素";
        if (!IsRoiEditingEnabled) { InteractionMode = ImageInteractionMode.Pan; _dragAction = DragAction.None; _workingRoi = null; }
    }
    public static readonly DependencyProperty SourceImageProperty = DependencyProperty.Register(nameof(SourceImage), typeof(ImageFrame),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty OverlaysProperty = DependencyProperty.Register(nameof(Overlays), typeof(IReadOnlyList<OverlayItem>),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(null, (d, _) => ((ImageViewportControl)d)._canvas?.InvalidateVisual()));
    public static readonly DependencyProperty EditableRoiProperty = DependencyProperty.Register(nameof(EditableRoi), typeof(RotatedRectRoi),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(null, OnRoiChanged));
    public static readonly DependencyProperty MeasurementOverlaysProperty = DependencyProperty.Register(nameof(MeasurementOverlays), typeof(IReadOnlyList<MeasurementOverlay>),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(null, (d, _) => ((ImageViewportControl)d)._canvas?.InvalidateVisual()));
    public static readonly DependencyProperty RecognitionOverlaysProperty = DependencyProperty.Register(nameof(RecognitionOverlays), typeof(IReadOnlyList<RecognitionOverlay>),
        typeof(ImageViewportControl), new FrameworkPropertyMetadata(null, (d, _) => ((ImageViewportControl)d)._canvas?.InvalidateVisual()));
    public IReadOnlyList<RecognitionOverlay>? RecognitionOverlays { get => (IReadOnlyList<RecognitionOverlay>?)GetValue(RecognitionOverlaysProperty); set => SetValue(RecognitionOverlaysProperty, value); }
    public ImageFrame? SourceImage { get => (ImageFrame?)GetValue(SourceImageProperty); set => SetValue(SourceImageProperty, value); }
    public IReadOnlyList<OverlayItem>? Overlays { get => (IReadOnlyList<OverlayItem>?)GetValue(OverlaysProperty); set => SetValue(OverlaysProperty, value); }
    public RotatedRectRoi? EditableRoi { get => (RotatedRectRoi?)GetValue(EditableRoiProperty); set => SetValue(EditableRoiProperty, value); }
    public IReadOnlyList<MeasurementOverlay>? MeasurementOverlays { get => (IReadOnlyList<MeasurementOverlay>?)GetValue(MeasurementOverlaysProperty); set => SetValue(MeasurementOverlaysProperty, value); }
    public event Action<RotatedRectRoi>? RoiChanged;
    public event Action<string>? PreviewError;
    public ImageViewTransform ViewTransform { get; } = new();
    public ImageInteractionMode InteractionMode { get; set; } = ImageInteractionMode.Select;
    public long PreviewBytes => _cache.Bytes;
    public long MaxPreviewBytes => _cache.MaximumBytes;
    public int PreviewWidth => _bitmap?.PixelWidth ?? 0;
    public int PreviewHeight => _bitmap?.PixelHeight ?? 0;
    public bool IsPreviewLoading { get; private set; }
    public long DisplayedSourceBytes => _frame?.BufferHandle.Length ?? 0;
    public int CurrentPyramidLevel { get; private set; } = -1;
    public RectRoi? VisibleImageRegion { get; private set; }
    public long TileCacheBytes => _tiles.Bytes;
    public long MaxTileCacheBytes => _tiles.MaximumBytes;
    public long DisplayCacheBytes => PreviewBytes + TileCacheBytes;
    public long MaxDisplayCacheBytes => MaxPreviewBytes + MaxTileCacheBytes;
    public long TileRequests => _tiles.Requests;
    public long TileCacheHits => _tiles.Hits;
    public long CancelledTileBatches { get; private set; }
    public bool IsTileLoading { get; private set; }
    public double ViewportWidth => _canvas.ActualWidth;
    public double ViewportHeight => _canvas.ActualHeight;
    public Rect ViewportBounds => new(_canvas.TranslatePoint(new Point(0, 0), this), _canvas.RenderSize);
    public double DevicePixelScaleX => VisualTreeHelper.GetDpi(this).DpiScaleX;
    public IReadOnlyList<VisibleImageTile> VisibleTiles
    {
        get
        {
            if (_frame is null || _disposed) return [];
            var visible = new List<VisibleImageTile>();
            foreach (var request in _visibleRequests)
                if (_tiles.TryGet(_frame, request.Region, request.Level, out var tile)) visible.Add(new(request.Region, request.Level, tile!));
            return visible.AsReadOnly();
        }
    }
    public bool IsNativeResolutionReady => !IsTileLoading && CurrentPyramidLevel == 0 && _visibleRequests.Length > 0 &&
        _frame is not null && _visibleRequests.All(r => _tiles.TryGet(_frame, r.Region, r.Level, out _));

    // Pixel payload budgets, excluding the original frame, WPF compositor allocations and bounded worker staging.
    private readonly PreviewCache _cache = new(16L * 1024 * 1024);
    private readonly ImageTileCache _tiles = new(48L * 1024 * 1024);
    private TileRequest[] _visibleRequests = [];
    private CancellationTokenSource? _tileCancellation;
    private Task _tileTask = Task.CompletedTask;
    private long _tileGeneration;
    private bool _suspendTileRequests;
    private readonly ImageCanvas _canvas;
    private readonly TextBlock _description, _coordinates;
    private ImageFrame? _frame;
    private BitmapSource? _bitmap;
    private CancellationTokenSource? _previewCancellation;
    private Task _previewTask = Task.CompletedTask;
    private readonly object _pendingGate = new();
    private readonly HashSet<Task> _pendingPreviews = new();
    private long _generation;
    private bool _disposed;
    private bool _fitMode = true;
    private Point _mouseStart, _previousMouse;
    private Point2D _imageStart;
    private RotatedRectRoi? _dragOriginal, _workingRoi;
    private DragAction _dragAction;
    private int _corner;
    private enum DragAction { None, Pan, Create, Move, Resize, Rotate }

    public ImageViewportControl()
    {
        MinHeight = 160;
        SetResourceReference(BackgroundProperty, "StudioPanel");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new WrapPanel { Margin = new Thickness(5, 3, 5, 3) };
        AddButton(tools, "选择", () => { InteractionMode = ImageInteractionMode.Select; UpdateDescription(); });
        AddButton(tools, "平移", () => { InteractionMode = ImageInteractionMode.Pan; UpdateDescription(); });
        _roiButtons.Add(AddButton(tools, "矩形 ROI", () => { InteractionMode = ImageInteractionMode.Rectangle; UpdateDescription(); }));
        _roiButtons.Add(AddButton(tools, "旋转矩形", () => { InteractionMode = ImageInteractionMode.RotatedRectangle; UpdateDescription(); }));
        AddButton(tools, "适应", FitToWindow);
        AddButton(tools, "1:1", ShowOneToOne);
        _description = new TextBlock { FontSize = 11, Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _description.SetResourceReference(TextBlock.ForegroundProperty, "StudioMutedForeground");
        tools.Children.Add(_description);
        _canvas = new ImageCanvas(this) { Focusable = true, ClipToBounds = true, Cursor = Cursors.Cross };
        RenderOptions.SetBitmapScalingMode(_canvas, BitmapScalingMode.NearestNeighbor);
        ViewTransform.Changed += ViewChanged;
        _canvas.MouseDown += MouseDownOnCanvas;
        _canvas.MouseMove += MouseMoveOnCanvas;
        _canvas.MouseUp += MouseUpOnCanvas;
        _canvas.MouseWheel += WheelOnCanvas;
        _canvas.LostMouseCapture += (_, _) => { _dragAction = DragAction.None; _workingRoi = null; _canvas.InvalidateVisual(); };
        _canvas.SizeChanged += (_, _) => { if (_fitMode) ViewTransform.Fit(_canvas.ActualWidth, _canvas.ActualHeight); else RequestVisibleTiles(); _canvas.InvalidateVisual(); UpdateDescription(); };
        _coordinates = new TextBlock { FontSize = 11, Padding = new Thickness(8, 4, 8, 4), Text = "滚轮缩放 · 中键拖动平移 · 选择 ROI 后拖动角点调整，圆点旋转" };
        _coordinates.SetResourceReference(TextBlock.ForegroundProperty, "StudioMutedForeground");
        grid.Children.Add(tools); Grid.SetRow(_canvas, 1); grid.Children.Add(_canvas);
        Grid.SetRow(_coordinates, 2); grid.Children.Add(_coordinates);
        Content = grid;
        Loaded += (_, _) => { if (_fitMode) FitToWindow(); };
    }

    private static Button AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(1), FontSize = 11 };
        button.Click += (_, _) => action(); panel.Children.Add(button);
        return button;
    }
    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ImageViewportControl)d).SetSource((ImageFrame?)e.NewValue);
    private static void OnRoiChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var owner = (ImageViewportControl)d; if (owner._dragAction == DragAction.None) owner._workingRoi = null; owner._canvas?.InvalidateVisual(); }

    private void SetSource(ImageFrame? source)
    {
        if (_disposed) return;
        _previewCancellation?.Cancel(); _previewCancellation?.Dispose();
        _tileCancellation?.Cancel(); _tileCancellation?.Dispose(); _tileCancellation = null;
        _tiles.Clear(); _cache.Clear(); _visibleRequests = []; VisibleImageRegion = null;
        CurrentPyramidLevel = -1; IsTileLoading = false; ++_tileGeneration;
        _previewCancellation = new CancellationTokenSource();
        var generation = ++_generation;
        var next = source?.Share();
        var old = _frame; _frame = next; old?.Dispose();
        _bitmap = null; _workingRoi = null; _dragAction = DragAction.None;
        if (next is null)
        {
            IsPreviewLoading = false; _previewTask = Task.CompletedTask; UpdateDescription(); _canvas.InvalidateVisual(); return;
        }
        _suspendTileRequests = true;
        try
        {
            ViewTransform.SetImage(next.Width, next.Height, next.CoordinateSpace.OriginX, next.CoordinateSpace.OriginY);
            if (_fitMode) ViewTransform.Fit(_canvas.ActualWidth, _canvas.ActualHeight);
            else if (old is null || old.Width != next.Width || old.Height != next.Height || old.CoordinateSpace != next.CoordinateSpace)
                ViewTransform.Pan((_canvas.ActualWidth - next.Width * ViewTransform.Zoom) / 2 - ViewTransform.PanX,
                    (_canvas.ActualHeight - next.Height * ViewTransform.Zoom) / 2 - ViewTransform.PanY);
        }
        finally { _suspendTileRequests = false; }
        IsPreviewLoading = true; UpdateDescription(); _canvas.InvalidateVisual();
        _previewTask = LoadPreviewAsync(next, generation, _previewCancellation.Token);
        lock (_pendingGate) _pendingPreviews.Add(_previewTask);
        _ = ForgetCompletedPreviewAsync(_previewTask);
        RequestVisibleTiles();
    }
    private async Task LoadPreviewAsync(ImageFrame source, long generation, CancellationToken ct)
    {
        using var retained = source.Share();
        try
        {
            var preview = await _cache.GetAsync(retained, ct);
            if (_disposed || ct.IsCancellationRequested || generation != _generation) return;
            _bitmap = preview.Bitmap;
            IsPreviewLoading = false;
            UpdateDescription(); _canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || generation != _generation) return;
            IsPreviewLoading = false; _description.Text = "预览失败：" + ex.Message; PreviewError?.Invoke(ex.Message);
        }
    }
    public Task WaitForPreviewAsync() => _previewTask;
    public Task WaitForTilesAsync() => _tileTask;
    public async Task WaitForDisplayAsync()
    {
        while (true)
        {
            var preview = _previewTask; var tiles = _tileTask;
            await Task.WhenAll(preview, tiles);
            if (ReferenceEquals(preview, _previewTask) && ReferenceEquals(tiles, _tileTask)) return;
        }
    }
    /// <summary>After Dispose/cancellation, asynchronously waits for every replaced preview to release its own frame shares.</summary>
    public Task DrainAsync()
    { lock (_pendingGate) return Task.WhenAll(_pendingPreviews.ToArray()); }
    private async Task ForgetCompletedPreviewAsync(Task task)
    { try { await task.ConfigureAwait(false); } finally { lock (_pendingGate) _pendingPreviews.Remove(task); } }
    public void FitToWindow()
    { _fitMode = true; ViewTransform.Fit(_canvas.ActualWidth, _canvas.ActualHeight); UpdateDescription(); _canvas.InvalidateVisual(); }
    public void ShowOneToOne()
    {
        _fitMode = false; _suspendTileRequests = true;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            ViewTransform.OneToOne(_canvas.ActualWidth, _canvas.ActualHeight);
            ViewTransform.ZoomAt(new(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2), 1 / dpi.DpiScaleX);
            ViewTransform.Pan(Math.Round(ViewTransform.PanX * dpi.DpiScaleX) / dpi.DpiScaleX - ViewTransform.PanX,
                Math.Round(ViewTransform.PanY * dpi.DpiScaleY) / dpi.DpiScaleY - ViewTransform.PanY);
        }
        finally { _suspendTileRequests = false; }
        RequestVisibleTiles(); UpdateDescription(); _canvas.InvalidateVisual();
    }
    public Point2D ScreenToImage(Point point) => ViewTransform.ToImage(point);
    public Point ImageToScreen(Point2D point) => ViewTransform.ToScreen(point);
    public void ClearPreviewCache()
    {
        _previewCancellation?.Cancel(); _tileCancellation?.Cancel(); ++_tileGeneration;
        _bitmap = null; _visibleRequests = []; _cache.Clear(); _tiles.Clear(); IsTileLoading = false; IsPreviewLoading = false;
        _canvas.InvalidateVisual(); UpdateDescription();
    }

    private void ViewChanged()
    {
        if (_suspendTileRequests || _disposed) return;
        RequestVisibleTiles(); UpdateDescription(); _canvas.InvalidateVisual();
    }
    private void RequestVisibleTiles()
    {
        if (_disposed || _suspendTileRequests) return;
        _tileCancellation?.Cancel(); _tileCancellation?.Dispose(); _tileCancellation = new();
        var generation = ++_tileGeneration;
        _visibleRequests = []; VisibleImageRegion = null;
        if (_frame is null || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
        { IsTileLoading = false; _tileTask = Task.CompletedTask; return; }
        var a = ViewTransform.ToImage(new(0, 0)); var b = ViewTransform.ToImage(new(_canvas.ActualWidth, _canvas.ActualHeight));
        var left = Math.Clamp((long)Math.Floor(a.X - _frame.CoordinateSpace.OriginX), 0, _frame.Width);
        var top = Math.Clamp((long)Math.Floor(a.Y - _frame.CoordinateSpace.OriginY), 0, _frame.Height);
        var right = Math.Clamp((long)Math.Ceiling(b.X - _frame.CoordinateSpace.OriginX), 0, _frame.Width);
        var bottom = Math.Clamp((long)Math.Ceiling(b.Y - _frame.CoordinateSpace.OriginY), 0, _frame.Height);
        if (right <= left || bottom <= top) { IsTileLoading = false; _tileTask = Task.CompletedTask; return; }
        VisibleImageRegion = new(_frame.CoordinateSpace.OriginX + left, _frame.CoordinateSpace.OriginY + top, right - left, bottom - top);
        var deviceZoom = ViewTransform.Zoom * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var level = Math.Clamp((int)Math.Floor(Math.Log2(1 / deviceZoom)), 0, 24);
        long span;
        // A bounded number of visible tiles avoids pinning arbitrary bitmap payload outside the LRU.
        // For exceptionally large virtual viewports, use a coarser layer and do not claim native readiness.
        while (true)
        {
            span = 512L << level;
            var count = ((right - 1) / span - left / span + 1) * ((bottom - 1) / span - top / span + 1);
            if (count <= 64 || level == 24) break;
            level++;
        }
        CurrentPyramidLevel = level;
        var requests = new List<TileRequest>();
        for (var tileY = top / span; tileY <= (bottom - 1) / span; tileY++)
            for (var tileX = left / span; tileX <= (right - 1) / span; tileX++)
            {
                var x = tileX * span; var y = tileY * span;
                requests.Add(new(new(_frame.CoordinateSpace.OriginX + x, _frame.CoordinateSpace.OriginY + y,
                    Math.Min(span, _frame.Width - x), Math.Min(span, _frame.Height - y)), level));
            }
        IsTileLoading = true;
        _tileTask = LoadTilesAsync(_frame, requests.ToArray(), generation, _tileCancellation.Token);
        lock (_pendingGate) _pendingPreviews.Add(_tileTask);
        _ = ForgetCompletedPreviewAsync(_tileTask);
    }
    private async Task LoadTilesAsync(ImageFrame source, TileRequest[] requests, long generation, CancellationToken ct)
    {
        using var retained = source.Share();
        try
        {
            await Task.WhenAll(requests.Select(r => _tiles.GetAsync(retained, r.Region, r.Level, ct)));
            if (_disposed || ct.IsCancellationRequested || generation != _tileGeneration) return;
            // Store geometry only. Rendering/diagnostics borrow the cache's bitmaps so eviction
            // does not leave a second long-lived bitmap collection outside the byte budget.
            _visibleRequests = requests; IsTileLoading = false; UpdateDescription(); _canvas.InvalidateVisual();
        }
        catch (OperationCanceledException) { CancelledTileBatches++; }
        catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || generation != _tileGeneration) return;
            IsTileLoading = false; _description.Text = "瓦片失败：" + ex.Message; PreviewError?.Invoke(ex.Message);
        }
    }

    private void UpdateDescription()
    {
        if (_description is null) return;
        if (_frame is null) { _description.Text = "尚未导入图像"; return; }
        var sampling = IsTileLoading ? " · 视口瓦片加载中（背景为预览）" : IsNativeResolutionReady ? " · L0 原始像素瓦片"
            : _visibleRequests.Length > 0 ? $" · L{CurrentPyramidLevel} 瓦片" : " · 缩略预览";
        _description.Text = $"{_frame.Width}×{_frame.Height} {_frame.PixelFormat} · {ViewTransform.Zoom:P1}" +
            (IsPreviewLoading ? " · 后台生成预览…" : sampling);
    }
    private Point2D ClampPoint(Point2D point)
    {
        var image = _frame!;
        return new(Math.Clamp(point.X, image.CoordinateSpace.OriginX, image.CoordinateSpace.OriginX + image.Width),
            Math.Clamp(point.Y, image.CoordinateSpace.OriginY, image.CoordinateSpace.OriginY + image.Height));
    }

    private void MouseDownOnCanvas(object sender, MouseButtonEventArgs e)
    {
        if (_frame is null) return;
        _canvas.Focus();
        _mouseStart = _previousMouse = e.GetPosition(_canvas); _imageStart = ClampPoint(ViewTransform.ToImage(_mouseStart));
        _dragOriginal = EditableRoi; _workingRoi = EditableRoi;
        if (!IsRoiEditingEnabled || e.ChangedButton is MouseButton.Middle or MouseButton.Right || InteractionMode == ImageInteractionMode.Pan)
            _dragAction = DragAction.Pan;
        else if (e.ChangedButton == MouseButton.Left && InteractionMode is ImageInteractionMode.Rectangle or ImageInteractionMode.RotatedRectangle)
        {
            _dragAction = DragAction.Create;
            _workingRoi = new(_imageStart.X, _imageStart.Y, 2, 2, 0, _frame.CoordinateSpace.Id);
        }
        else if (e.ChangedButton == MouseButton.Left && EditableRoi is { } roi)
        {
            var corners = roi.Corners();
            _corner = Array.FindIndex(corners, point => (ViewTransform.ToScreen(point) - _mouseStart).Length <= 9);
            if (_corner >= 0) _dragAction = DragAction.Resize;
            else if ((RotationHandle(roi) - _mouseStart).Length <= 10) _dragAction = DragAction.Rotate;
            else if (RoiInteractionGeometry.Contains(roi, _imageStart)) _dragAction = DragAction.Move;
            else _dragAction = DragAction.Pan;
        }
        else _dragAction = DragAction.Pan;
        _canvas.CaptureMouse(); e.Handled = true;
    }

    private void MouseMoveOnCanvas(object sender, MouseEventArgs e)
    {
        if (_frame is null) return;
        var position = e.GetPosition(_canvas);
        var pixel = ViewTransform.ToImage(position);
        ShowPixel(pixel);
        if (_dragAction == DragAction.None || !_canvas.IsMouseCaptured) return;
        if (_dragAction == DragAction.Pan)
        { _fitMode = false; ViewTransform.Pan(position.X - _previousMouse.X, position.Y - _previousMouse.Y); }
        else
        {
            var current = ClampPoint(pixel);
            RotatedRectRoi? proposal = _dragAction switch
            {
                DragAction.Create => new((_imageStart.X + current.X) / 2, (_imageStart.Y + current.Y) / 2,
                    Math.Max(2, Math.Abs(current.X - _imageStart.X)), Math.Max(2, Math.Abs(current.Y - _imageStart.Y)), 0, _frame.CoordinateSpace.Id),
                DragAction.Move when _dragOriginal is { } roi => RoiInteractionGeometry.Move(roi, current.X - _imageStart.X, current.Y - _imageStart.Y),
                DragAction.Resize when _dragOriginal is { } roi => RoiInteractionGeometry.ResizeCorner(roi, _corner, current),
                DragAction.Rotate when _dragOriginal is { } roi => RoiInteractionGeometry.RotateTo(roi, current),
                _ => null
            };
            if (proposal is not null && RoiInteractionGeometry.IsInside(proposal, _frame.Width, _frame.Height, _frame.CoordinateSpace))
                _workingRoi = proposal;
        }
        _previousMouse = position; _canvas.InvalidateVisual(); UpdateDescription();
    }
    private void MouseUpOnCanvas(object sender, MouseButtonEventArgs e)
    {
        if (_dragAction == DragAction.None) return;
        var action = _dragAction; var proposal = _workingRoi;
        _dragAction = DragAction.None; _workingRoi = null; _canvas.ReleaseMouseCapture();
        if (action is DragAction.Create or DragAction.Move or DragAction.Resize or DragAction.Rotate && proposal is not null)
        {
            try { RoiChanged?.Invoke(proposal); InteractionMode = ImageInteractionMode.Select; }
            catch (Exception ex) { _description.Text = "ROI 未应用：" + ex.Message; PreviewError?.Invoke(ex.Message); }
        }
        _canvas.InvalidateVisual(); e.Handled = true;
    }
    private void WheelOnCanvas(object sender, MouseWheelEventArgs e)
    {
        if (_frame is null) return;
        _fitMode = false; ViewTransform.ZoomAt(e.GetPosition(_canvas), e.Delta > 0 ? 1.2 : 1 / 1.2);
        _canvas.InvalidateVisual(); UpdateDescription(); e.Handled = true;
    }

    private void ShowPixel(Point2D point)
    {
        var image = _frame!;
        var x = (int)Math.Floor(point.X - image.CoordinateSpace.OriginX); var y = (int)Math.Floor(point.Y - image.CoordinateSpace.OriginY);
        if ((uint)x >= (uint)image.Width || (uint)y >= (uint)image.Height) return;
        using var pixels = image.Lease(); var row = pixels.GetRowSpan(y);
        var value = image.PixelFormat switch
        {
            VisionPlatform.Contracts.Images.PixelFormat.RGB8 => $"RGB=({row[x * 3]},{row[x * 3 + 1]},{row[x * 3 + 2]})",
            VisionPlatform.Contracts.Images.PixelFormat.BGR8 => $"BGR=({row[x * 3]},{row[x * 3 + 1]},{row[x * 3 + 2]})",
            VisionPlatform.Contracts.Images.PixelFormat.BayerRG8 or VisionPlatform.Contracts.Images.PixelFormat.BayerGB8 => $"RAW={row[x]}（原始 Bayer）",
            _ => $"灰度={row[x]}"
        };
        _coordinates.Text = $"原图 X={image.CoordinateSpace.OriginX + x:F0}, Y={image.CoordinateSpace.OriginY + y:F0} · {value} · 显示缓存 {DisplayCacheBytes / 1048576.0:F1}/{MaxDisplayCacheBytes / 1048576} MiB";
    }
    private Point RotationHandle(RotatedRectRoi roi)
    {
        var angle = roi.AngleDegrees * Math.PI / 180; var length = roi.Height / 2 + 24 / ViewTransform.Zoom;
        return ViewTransform.ToScreen(new(roi.CenterX + Math.Sin(angle) * length, roi.CenterY - Math.Cos(angle) * length));
    }
    private Brush Palette(string key) => ThemeResources.Brush(this, key);

    private void Draw(DrawingContext drawing, Size size)
    {
        drawing.DrawRectangle(Palette("StudioImageCanvas"), null, new Rect(size));
        if (_frame is null)
        {
            DrawText(drawing, "导入图像或运行节点以查看结果", new(24, 24), Palette("StudioMutedForeground")); return;
        }
        if (_bitmap is not null)
            drawing.DrawImage(_bitmap, new Rect(ViewTransform.PanX, ViewTransform.PanY, _frame.Width * ViewTransform.Zoom, _frame.Height * ViewTransform.Zoom));
        else DrawText(drawing, IsPreviewLoading ? "正在后台生成图像预览…" : "无可用预览", new(24, 24), Palette("StudioMutedForeground"));
        foreach (var request in _visibleRequests)
            if (_tiles.TryGet(_frame, request.Region, request.Level, out var tile))
                drawing.DrawImage(tile!.Bitmap, new Rect(ViewTransform.ToScreen(new(request.Region.X, request.Region.Y)),
                    new Size(request.Region.Width * ViewTransform.Zoom, request.Region.Height * ViewTransform.Zoom)));
        if (Overlays is { } overlays)
        {
            foreach (var overlay in overlays)
            {
                if (overlay.Region.CoordinateSpaceId != _frame.CoordinateSpace.Id) continue;
                var brush = !overlay.IsValid ? Palette("StudioMutedForeground") : overlay.Kind switch
                {
                    OverlayKind.ReferenceRoi => Palette("StudioWarning"),
                    OverlayKind.FollowedRoi => Palette("StudioSuccess"),
                    OverlayKind.Match => Palette("StudioPrimary"),
                    _ => Palette("StudioFailure")
                };
                DrawRoi(drawing, overlay.Region, brush, !overlay.IsValid, overlay.Label);
            }
        }
        if ((_workingRoi ?? EditableRoi) is { } editable && editable.CoordinateSpaceId == _frame.CoordinateSpace.Id)
        {
            DrawRoi(drawing, editable, Palette("StudioWarning"), false, "编辑 ROI");
            foreach (var corner in editable.Corners()) drawing.DrawEllipse(Palette("StudioWarning"), new Pen(Palette("StudioForeground"), 1), ViewTransform.ToScreen(corner), 4, 4);
            var angle = editable.AngleDegrees * Math.PI / 180;
            var top = ViewTransform.ToScreen(new(editable.CenterX + Math.Sin(angle) * editable.Height / 2, editable.CenterY - Math.Cos(angle) * editable.Height / 2));
            var handle = RotationHandle(editable); drawing.DrawLine(new Pen(Palette("StudioWarning"), 1), top, handle);
            drawing.DrawEllipse(Palette("StudioPanel"), new Pen(Palette("StudioWarning"), 1), handle, 5, 5);
        }
        DrawMeasurements(drawing);
        DrawRecognition(drawing);
        DrawCollections(drawing, size);
    }
    private void DrawRecognition(DrawingContext drawing)
    {
        if (_frame is null || RecognitionOverlays is null) return;
        var brush = Palette("StudioPrimary"); var pen = new Pen(brush, 1.5); var budget = 8192;
        foreach (var overlay in RecognitionOverlays.Take(256))
        {
            if (budget <= 0) break;
            if (overlay.CoordinateSpaceId != _frame.CoordinateSpace.Id || overlay.Points is null) continue;
            var points = overlay.Points.Take(budget).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).Select(ViewTransform.ToScreen).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
            budget -= points.Length; if (points.Length == 0) continue;
            if (overlay.Kind is RecognitionOverlayKind.Polygon or RecognitionOverlayKind.Polyline && points.Length > 1)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open()) { path.BeginFigure(points[0], false, overlay.Kind == RecognitionOverlayKind.Polygon); path.PolyLineTo(points.Skip(1).ToArray(), true, false); }
                geometry.Freeze(); drawing.DrawGeometry(null, pen, geometry);
            }
            else if (overlay.Kind == RecognitionOverlayKind.Points)
                foreach (var p in points) drawing.DrawEllipse(null, pen, p, 3, 3);
            var label = overlay.Label ?? ""; if (label.Length > 256) label = label[..256] + "…";
            if (label.Length > 0) DrawText(drawing, label, new Point(points[0].X + 5, points[0].Y - 18), brush);
        }
    }
    private void DrawMeasurements(DrawingContext drawing)
    {
        if (_frame is null || MeasurementOverlays is null) return;
        var brush = Palette("StudioSuccess"); var pen = new Pen(brush, 1.5); var remainingPoints = 8192;
        foreach (var overlay in MeasurementOverlays.Take(256))
        {
            if (remainingPoints <= 0) break;
            if (overlay.CoordinateSpaceId != _frame.CoordinateSpace.Id || overlay.Points is null) continue;
            var points = overlay.Points.Take(remainingPoints).Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToArray();
            remainingPoints -= points.Length;
            if (points.Length == 0) continue;
            var label = overlay.Label ?? ""; if (label.Length > 256) label = label[..256] + "…";
            var anchor = ViewTransform.ToScreen(points[0]);
            switch (overlay.Kind)
            {
                case MeasurementOverlayKind.Points:
                    foreach (var point in points)
                    {
                        var p = ViewTransform.ToScreen(point);
                        drawing.DrawLine(pen, new Point(p.X - 3, p.Y), new Point(p.X + 3, p.Y));
                        drawing.DrawLine(pen, new Point(p.X, p.Y - 3), new Point(p.X, p.Y + 3));
                    }
                    break;
                case MeasurementOverlayKind.Line when points.Length >= 2:
                    drawing.DrawLine(pen, anchor, ViewTransform.ToScreen(points[1])); break;
                case MeasurementOverlayKind.Circle when double.IsFinite(overlay.Radius) && overlay.Radius > 0:
                    var radius = overlay.Radius * ViewTransform.Zoom;
                    if (double.IsFinite(radius)) drawing.DrawEllipse(null, pen, anchor, radius, radius); break;
            }
            if (label.Length > 0) DrawText(drawing, label, new(anchor.X + 5, anchor.Y - 18), brush);
        }
    }
    private void DrawRoi(DrawingContext drawing, RotatedRectRoi roi, Brush color, bool invalid, string label)
    {
        var points = roi.Corners().Select(ViewTransform.ToScreen).ToArray();
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open()) { builder.BeginFigure(points[0], false, true); builder.PolyLineTo(points.Skip(1).ToArray(), true, true); }
        geometry.Freeze(); var pen = new Pen(color, 1.5); if (invalid) pen.DashStyle = DashStyles.Dash;
        drawing.DrawGeometry(null, pen, geometry);
        DrawText(drawing, invalid ? label + "（失效）" : label, new(points[0].X + 4, points[0].Y - 17), color);
    }
    private void DrawText(DrawingContext drawing, string text, Point point, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawRectangle(Palette("StudioOverlayLabelBackground"), null, new Rect(point, new Size(formatted.Width + 4, formatted.Height)));
        drawing.DrawText(formatted, point);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_generation; _previewCancellation?.Cancel(); _previewCancellation?.Dispose(); _previewCancellation = null;
        ++_tileGeneration; _tileCancellation?.Cancel(); _tileCancellation?.Dispose(); _tileCancellation = null;
        ViewTransform.Changed -= ViewChanged; _visibleRequests = []; VisibleImageRegion = null; IsTileLoading = false;
        IsPreviewLoading = false;
        _frame?.Dispose(); _frame = null; _bitmap = null; _cache.Dispose(); _tiles.Dispose();
        _canvas.ReleaseMouseCapture(); _canvas.InvalidateVisual();
    }
    private sealed class ImageCanvas(ImageViewportControl owner) : FrameworkElement
    { protected override void OnRender(DrawingContext drawingContext) { base.OnRender(drawingContext); owner.Draw(drawingContext, RenderSize); } }
}

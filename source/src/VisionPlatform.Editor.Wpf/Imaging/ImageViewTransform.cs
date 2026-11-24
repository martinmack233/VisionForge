using System.Windows;
using VisionPlatform.Contracts.Geometry;

namespace VisionPlatform.Editor.Wpf.Imaging;

/// <summary>Display-only transform. One image pixel is one WPF DIP at Zoom=1; image coordinates stay original.</summary>
public sealed class ImageViewTransform
{
    public event Action? Changed;
    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    public double OriginX { get; private set; }
    public double OriginY { get; private set; }
    public int ImageWidth { get; private set; }
    public int ImageHeight { get; private set; }

    public void SetImage(int width, int height, double originX = 0, double originY = 0)
    { ImageWidth = width; ImageHeight = height; OriginX = originX; OriginY = originY; Changed?.Invoke(); }
    public Point ToScreen(Point2D point) => new(PanX + (point.X - OriginX) * Zoom, PanY + (point.Y - OriginY) * Zoom);
    public Point2D ToImage(Point point) => new(OriginX + (point.X - PanX) / Zoom, OriginY + (point.Y - PanY) / Zoom);
    public void Fit(double viewportWidth, double viewportHeight)
    {
        if (ImageWidth <= 0 || ImageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0) return;
        Zoom = Math.Clamp(Math.Min(Math.Max(1, viewportWidth - 24) / ImageWidth, Math.Max(1, viewportHeight - 24) / ImageHeight), .0001, 64);
        Center(viewportWidth, viewportHeight);
        Changed?.Invoke();
    }
    public void OneToOne(double viewportWidth, double viewportHeight)
    { Zoom = 1; Center(viewportWidth, viewportHeight); Changed?.Invoke(); }
    public void ZoomAt(Point point, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        var original = ToImage(point);
        Zoom = Math.Clamp(Zoom * factor, .0001, 64);
        PanX = point.X - (original.X - OriginX) * Zoom;
        PanY = point.Y - (original.Y - OriginY) * Zoom;
        Changed?.Invoke();
    }
    public void Pan(double dx, double dy) { PanX += dx; PanY += dy; Changed?.Invoke(); }
    private void Center(double width, double height)
    { PanX = (width - ImageWidth * Zoom) / 2; PanY = (height - ImageHeight * Zoom) / 2; }
}

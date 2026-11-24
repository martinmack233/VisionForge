using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Editor.Wpf.Imaging;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase4AMeasurementRenderTests
{
    [Fact]
    public void MeasurementGeometryUsesOriginalPixelsAndMismatchedSpacesAreNotDrawn()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var baseline = FrameHandle.ActiveBytes;
                    using var source = new ImageFrameBuilder(256, 256, PixelFormat.Mono8).Build();
                    using var viewport = new ImageViewportControl { Width = 512, Height = 400, SourceImage = source };
                    viewport.Measure(new Size(512, 400)); viewport.Arrange(new Rect(0, 0, 512, 400)); viewport.UpdateLayout();
                    viewport.ShowOneToOne(); await viewport.WaitForDisplayAsync();
                    viewport.MeasurementOverlays = [new(MeasurementOverlayKind.Circle, "r40", [new(128, 128)], "image", 40),
                        new(MeasurementOverlayKind.Line, "line", [new(50, 50), new(100, 50)]),
                        new(MeasurementOverlayKind.Points, "point", [new(80, 180)])];
                    await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
                    var withOverlay = Render(viewport);
                    foreach (var point in new[] { new VisionPlatform.Contracts.Geometry.Point2D(168, 128), new(75, 50), new(80, 180) })
                    {
                        var at = viewport.ImageToScreen(point); at.Offset(viewport.ViewportBounds.X, viewport.ViewportBounds.Y);
                        Assert.True(HasGreen(withOverlay, (int)Math.Round(at.X), (int)Math.Round(at.Y)), "Measurement geometry was not rendered at its original-image coordinate.");
                    }
                    viewport.MeasurementOverlays = [new(MeasurementOverlayKind.Circle, "wrong space", [new(128, 128)], "another-image", 40)];
                    await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle); var hidden = Render(viewport);
                    var location = viewport.ImageToScreen(new(168, 128)); location.Offset(viewport.ViewportBounds.X, viewport.ViewportBounds.Y);
                    Assert.False(HasGreen(hidden, (int)Math.Round(location.X), (int)Math.Round(location.Y)));
                    viewport.Dispose(); await viewport.DrainAsync(); Assert.False(source.IsDisposed);
                    source.Dispose(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
                }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static RenderTargetBitmap Render(ImageViewportControl viewport)
    {
        var bitmap = new RenderTargetBitmap(512, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport); return bitmap;
    }
    private static bool HasGreen(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[512 * 400 * 4]; bitmap.CopyPixels(bytes, 512 * 4, 0);
        for (var dy = -2; dy <= 2; dy++) for (var dx = -2; dx <= 2; dx++)
        {
            var px = x + dx; var py = y + dy; if (px < 0 || py < 0 || px >= 512 || py >= 400) continue;
            var at = (py * 512 + px) * 4;
            if (bytes[at + 1] > 60 && bytes[at + 1] > bytes[at + 2] * 1.4 && bytes[at + 1] > bytes[at] * 1.1) return true;
        }
        return false;
    }
}

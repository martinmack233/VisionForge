using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Wpf.Imaging;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase4BRecognitionRenderTests
{
    [Fact]
    public void ActualPolygonAndPointsRenderAtOriginalPixelsWithoutChangingOrReleasingBorrowedImage()
    {
        RunSta(async dispatcher =>
        {
            var initialBytes = FrameHandle.ActiveBytes;
            using var builder = new ImageFrameBuilder(256, 256, PixelFormat.Mono8, 273, new("sensor", 1000, 2000));
            builder.GetRowSpan(17)[29] = 83;
            using var source = builder.Build(); var before = Pixels(source);
            using var viewport = await CreateViewport(source);
            viewport.RecognitionOverlays = [
                new(RecognitionOverlayKind.Polygon, "", [new(1040, 2040), new(1100, 2040), new(1100, 2100)], "sensor"),
                new(RecognitionOverlayKind.Points, "", [new(1180, 2180)], "sensor")];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var rendered = Render(viewport);
            // Horizontal polygon edge, closing diagonal, and the true decoder localization point.
            foreach (var point in new Point2D[] { new(1070, 2040), new(1070, 2070), new(1180, 2180) })
                Assert.True(HasBlue(rendered, At(viewport, point)), $"Recognition geometry did not render at original pixel {point}.");
            Assert.Equal(before, Pixels(source));
            Assert.False(source.IsDisposed);
            viewport.Dispose(); await viewport.DrainAsync();
            Assert.False(source.IsDisposed); Assert.Equal(1, source.BufferHandle.ReferenceCount);
            Assert.Equal(before, Pixels(source));
            source.Dispose(); Assert.Equal(initialBytes, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void WrongCoordinateSpaceIsHiddenAndReplacingOverlaysRemovesOldDrawing()
    {
        RunSta(async dispatcher =>
        {
            using var source = new ImageFrameBuilder(256, 256, PixelFormat.Mono8).Build();
            using var viewport = await CreateViewport(source);
            var anchor = new Point2D(128, 128);
            viewport.RecognitionOverlays = [new(RecognitionOverlayKind.Points, "", [anchor])];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.True(HasBlue(Render(viewport), At(viewport, anchor)));
            viewport.RecognitionOverlays = [new(RecognitionOverlayKind.Points, "", [anchor], "wrong-image")];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.False(HasBlue(Render(viewport), At(viewport, anchor)));
            viewport.RecognitionOverlays = null;
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.False(HasBlue(Render(viewport), At(viewport, anchor)));
            viewport.Dispose(); await viewport.DrainAsync();
        });
    }

    [Fact]
    public void LongLabelsAndLargeOverlayAndPointCollectionsAreBoundedDuringActualRendering()
    {
        RunSta(async dispatcher =>
        {
            using var source = new ImageFrameBuilder(256, 256, PixelFormat.Mono8).Build();
            using var viewport = await CreateViewport(source);
            var anchor = new Point2D(40, 60);
            var longLabel = new string('Z', 1_000_000);
            viewport.RecognitionOverlays = [new(RecognitionOverlayKind.Label, longLabel, [anchor])];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var longRendered = Render(viewport);
            viewport.RecognitionOverlays = [new(RecognitionOverlayKind.Label, new string('Z', 256) + "…", [anchor])];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            Assert.Equal(BitmapPixels(Render(viewport)), BitmapPixels(longRendered));

            var safe = new RecognitionOverlay(RecognitionOverlayKind.Points, "", [new(70, 90)]);
            var unseen = new Point2D(190, 190);
            var overlays = new CountingList<RecognitionOverlay>(100_000, i => i < 256 ? safe :
                new(RecognitionOverlayKind.Points, "", [unseen]));
            viewport.RecognitionOverlays = overlays;
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var limitedOverlays = Render(viewport);
            Assert.InRange(overlays.Reads, 1, 256);
            Assert.True(HasBlue(limitedOverlays, At(viewport, new(70, 90))));
            Assert.False(HasBlue(limitedOverlays, At(viewport, unseen)));

            var points = new CountingList<Point2D>(1_000_000, i => i < 8192 ? new(90, 110) : unseen);
            viewport.RecognitionOverlays = [new(RecognitionOverlayKind.Points, "", points)];
            await dispatcher.InvokeAsync(viewport.UpdateLayout, DispatcherPriority.ApplicationIdle);
            var limitedPoints = Render(viewport);
            Assert.InRange(points.Reads, 1, 8192);
            Assert.True(HasBlue(limitedPoints, At(viewport, new(90, 110))));
            Assert.False(HasBlue(limitedPoints, At(viewport, unseen)));
            Assert.InRange(viewport.DisplayCacheBytes, 0, viewport.MaxDisplayCacheBytes);
            viewport.Dispose(); await viewport.DrainAsync();
        });
    }

    private static async Task<ImageViewportControl> CreateViewport(ImageFrame source)
    {
        var viewport = new ImageViewportControl { Width = 512, Height = 400, SourceImage = source };
        viewport.Measure(new Size(512, 400)); viewport.Arrange(new Rect(0, 0, 512, 400)); viewport.UpdateLayout();
        viewport.ShowOneToOne(); await viewport.WaitForDisplayAsync();
        return viewport;
    }
    private static Point At(ImageViewportControl viewport, Point2D point)
    {
        var screen = viewport.ImageToScreen(point); screen.Offset(viewport.ViewportBounds.X, viewport.ViewportBounds.Y); return screen;
    }
    private static RenderTargetBitmap Render(ImageViewportControl viewport)
    {
        var bitmap = new RenderTargetBitmap(512, 400, 96, 96, PixelFormats.Pbgra32); bitmap.Render(viewport); return bitmap;
    }
    private static byte[] BitmapPixels(BitmapSource bitmap)
    {
        var bytes = new byte[512 * 400 * 4]; bitmap.CopyPixels(bytes, 512 * 4, 0); return bytes;
    }
    private static bool HasBlue(BitmapSource bitmap, Point at)
    {
        var bytes = BitmapPixels(bitmap); var x = (int)Math.Round(at.X); var y = (int)Math.Round(at.Y);
        for (var dy = -4; dy <= 4; dy++) for (var dx = -4; dx <= 4; dx++)
        {
            var px = x + dx; var py = y + dy;
            if (px < 0 || py < 0 || px >= 512 || py >= 400) continue;
            var offset = (py * 512 + px) * 4;
            if (bytes[offset] > 90 && bytes[offset] > bytes[offset + 1] * 1.15 && bytes[offset] > bytes[offset + 2] * 1.3) return true;
        }
        return false;
    }
    private static byte[] Pixels(ImageFrame image)
    {
        using var lease = image.Lease(); var bytes = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++) lease.GetRowSpan(y).CopyTo(bytes.AsSpan(y * image.Width, image.Width)); return bytes;
    }
    private static void RunSta(Func<Dispatcher, Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(dispatcher); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class CountingList<T>(int count, Func<int, T> value) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }
        public int Count => count;
        public T this[int index] { get { Reads++; return value(index); } }
        public IEnumerator<T> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Imaging;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;
using PixelFormatConverter = VisionPlatform.Imaging.PixelFormatConverter;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase3TileTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public async Task NativeTileMatchesOriginalOrFullDemosaicAcrossOddViewAndTileBoundaries(PixelFormat format)
    {
        using var image = Fixture(format);
        using var view = image.CreateView(1, 1, 38, 34);
        using var before = image.Lease(); var unchanged = before.GetRowSpan(12).ToArray();
        var provider = new OpenCvImageTileProvider();
        using var reference = PixelFormatConverter.Convert(view, format == PixelFormat.Mono8 ? PixelFormat.Mono8 : PixelFormat.BGR8);
        using var pixels = reference.Image.Lease();
        foreach (var (x, width) in new[] { (3, 13), (16, 15) })
        {
            var tile = await provider.RequestTileAsync(view, new(view.CoordinateSpace.OriginX + x, view.CoordinateSpace.OriginY + 5, width, 15), 0, default);
            Assert.True(tile.Bitmap.IsFrozen); Assert.Equal(width, tile.Bitmap.PixelWidth); Assert.Equal(15, tile.Bitmap.PixelHeight);
            var channels = format == PixelFormat.Mono8 ? 1 : 3; var actual = Copy(tile);
            for (var y = 0; y < 15; y++)
                Assert.Equal(pixels.GetRowSpan(y + 5).Slice(x * channels, width * channels).ToArray(), actual.AsSpan(y * width * channels, width * channels).ToArray());
        }
        Assert.Equal(unchanged, before.GetRowSpan(12).ToArray()); Assert.False(image.IsDisposed);
    }

    [Fact]
    public async Task PyramidLayersAreIndependentAndLruNeverRetainsImageLeases()
    {
        var initial = FrameHandle.ActiveBytes;
        using var cache = new ImageTileCache(1024 * 1024);
        using (var builder = new ImageFrameBuilder(1536, 512, PixelFormat.BGR8))
        using (var image = builder.Build())
        {
            for (var x = 0; x < 3; x++)
            {
                await cache.GetAsync(image, new(x * 512, 0, 512, 512), 0, default);
                Assert.InRange(cache.Bytes, 1, cache.MaximumBytes);
                Assert.Equal(1, cache.Count);
            }
            Assert.False(cache.TryGet(image, new(0, 0, 512, 512), 0, out _));
            Assert.True(cache.TryGet(image, new(1024, 0, 512, 512), 0, out var native)); Assert.Equal(512, native!.Bitmap.PixelWidth);
            await cache.GetAsync(image, new(1024, 0, 512, 512), 1, default);
            Assert.True(cache.TryGet(image, new(1024, 0, 512, 512), 1, out var lower)); Assert.Equal(256, lower!.Bitmap.PixelWidth);
            Assert.True(cache.TryGet(image, new(1024, 0, 512, 512), 0, out _));
            var hits = cache.Hits; await cache.GetAsync(image, new(1024, 0, 512, 512), 1, default); Assert.Equal(hits + 1, cache.Hits);
        }
        Assert.Equal(initial, FrameHandle.ActiveBytes); cache.Clear(); Assert.Equal(0, cache.Bytes);
    }

    [Theory]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public async Task LowerBayerLayerMatchesBoundedBilinearThenAreaReference(PixelFormat format)
    {
        using var image = Fixture(format); var provider = new OpenCvImageTileProvider();
        var tile = await provider.RequestTileAsync(image, new(54, 80, 32, 24), 2, default);
        using var reference = PixelFormatConverter.Convert(image, PixelFormat.BGR8);
        using var lease = new OpenCvImageLease(reference.Image);
        using var core = new Mat(lease.Mat, new OpenCvSharp.Rect(4, 5, 32, 24)); using var resized = new Mat();
        Cv2.Resize(core, resized, new OpenCvSharp.Size(8, 6), 0, 0, InterpolationFlags.Area);
        var actual = Copy(tile); var expected = new byte[24];
        for (var y = 0; y < 6; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(resized.Ptr(y), expected, 0, expected.Length);
            for (var x = 0; x < expected.Length; x++) Assert.InRange(Math.Abs(expected[x] - actual[y * 24 + x]), 0, 1);
        }
    }

    [Fact]
    public async Task CacheClearRejectsLateInsertAndCancellationReleasesProviderShare()
    {
        using var source = new ImageFrameBuilder(512, 512, PixelFormat.Mono8).Build();
        var delayed = new DelayedTileProvider(); using var cache = new ImageTileCache(1024 * 1024, delayed);
        var request = cache.GetAsync(source, new(0, 0, 512, 512), 0, default);
        cache.Clear(); delayed.Complete(); await request;
        Assert.Equal(0, cache.Count); Assert.Equal(0, cache.Bytes);
        var before = FrameHandle.ActiveBytes; using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenCvImageTileProvider().RequestTileAsync(source, new(0, 0, 512, 512), 0, cancel.Token));
        Assert.Equal(before, FrameHandle.ActiveBytes); Assert.False(source.IsDisposed);
    }

    [Fact]
    public async Task NativeOneToOneActuallyShowsHighFrequencyOriginalPixelsOf82MegapixelImage()
    {
        await RunSta(async () =>
        {
            var before = FrameHandle.ActiveBytes; using var control = CreateControl();
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8))
            using (var image = BuildHighFrequency(builder))
            {
                control.SourceImage = image; await control.WaitForDisplayAsync();
                Assert.True(control.CurrentPyramidLevel > 0);
                control.ShowOneToOne(); await control.WaitForTilesAsync();
                Assert.Equal(0, control.CurrentPyramidLevel); Assert.True(control.IsNativeResolutionReady);
                Assert.InRange(control.ViewTransform.Zoom * control.DevicePixelScaleX, .999999, 1.000001);
                Assert.True(control.VisibleImageRegion!.Width * control.VisibleImageRegion.Height < image.Width * (double)image.Height / 20);
                Assert.InRange(control.VisibleTiles.Count, 1, 64); Assert.InRange(control.DisplayCacheBytes, 1, control.MaxDisplayCacheBytes);
                var tile = Assert.Single(control.VisibleTiles, t => t.Region.X <= 4096 && t.Region.X + t.Region.Width > 4096 && t.Region.Y <= 5000 && t.Region.Y + t.Region.Height > 5000);
                var actual = Copy(tile.Preview); var x = 4096 - (int)tile.Region.X; var y = 5000 - (int)tile.Region.Y;
                Assert.Equal((byte)0, actual[y * tile.Preview.Bitmap.PixelWidth + x]);
                Assert.Equal((byte)255, actual[y * tile.Preview.Bitmap.PixelWidth + x + 1]);
                // A thumbnail average cannot preserve this alternating one-pixel pattern.
                for (var offset = 0; offset < 16; offset++) Assert.Equal((byte)(offset % 2 * 255), actual[y * tile.Preview.Bitmap.PixelWidth + x + offset]);
                await Dispatcher.CurrentDispatcher.InvokeAsync(control.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var rendered = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); rendered.Render(control);
                var sample = control.ImageToScreen(new(4096.5, 5000.5)); var bounds = control.ViewportBounds;
                var screenPixels = new byte[8];
                rendered.CopyPixels(new Int32Rect((int)Math.Floor(bounds.X + sample.X), (int)Math.Floor(bounds.Y + sample.Y), 2, 1), screenPixels, 8, 0);
                Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 }, screenPixels);
                var point = new Point2D(4100.25, 5001.75); var region = control.VisibleImageRegion;
                control.ViewTransform.Pan(300, -100); await control.WaitForTilesAsync();
                Assert.NotEqual(region, control.VisibleImageRegion); Assert.True(control.IsNativeResolutionReady);
                var screen = control.ImageToScreen(point); Assert.Equal(point.X, control.ScreenToImage(screen).X, 8); Assert.Equal(point.Y, control.ScreenToImage(screen).Y, 8);
                control.ViewTransform.ZoomAt(screen, .25); await control.WaitForTilesAsync();
                Assert.Equal(2, control.CurrentPyramidLevel); Assert.False(control.IsNativeResolutionReady);
                Assert.InRange(control.DisplayCacheBytes, 1, control.MaxDisplayCacheBytes);
                Assert.False(image.IsDisposed);
            }
            control.Dispose(); await control.DrainAsync(); Assert.Equal(before, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public async Task RapidNativePanAndSourceSwitchReleaseAllOldFramesAndCaches()
    {
        await RunSta(async () =>
        {
            var before = FrameHandle.ActiveBytes; var control = CreateControl();
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.BayerGB8))
            using (var first = builder.Build())
            using (var second = Fixture(PixelFormat.RGB8))
            {
                control.SourceImage = first; control.ShowOneToOne();
                for (var i = 0; i < 8; i++) control.ViewTransform.Pan(120, 90);
                control.SourceImage = second; await control.WaitForDisplayAsync();
                Assert.All(control.VisibleTiles, tile => Assert.InRange(tile.Region.X, 50, 90));
                Assert.True(control.IsNativeResolutionReady); Assert.True(control.CancelledTileBatches > 0);
                control.SourceImage = null; Assert.Equal(0, control.DisplayCacheBytes); Assert.Empty(control.VisibleTiles);
                control.Dispose(); await control.DrainAsync(); Assert.False(first.IsDisposed); Assert.False(second.IsDisposed);
            }
            Assert.Equal(before, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public async Task TileRegionsAndLayerLimitsAreValidatedWithoutChangingInput()
    {
        using var image = Fixture(PixelFormat.Mono8); var provider = new OpenCvImageTileProvider();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.RequestTileAsync(image, new(49, 75, 10, 10), 0, default));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RequestTileAsync(image, new(50.5, 75, 10, 10), 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.RequestTileAsync(image, new(50, 75, 10, 10), -1, default));
        using var huge = new ImageFrameBuilder(513, 513, PixelFormat.Mono8).Build();
        await Assert.ThrowsAsync<ArgumentException>(() => provider.RequestTileAsync(huge, new(0, 0, 513, 513), 0, default));
        var layer = await provider.RequestTileAsync(huge, new(0, 0, 513, 513), 1, default); Assert.Equal(257, layer.Bitmap.PixelWidth);
        Assert.False(image.IsDisposed);
    }

    private static ImageViewportControl CreateControl()
    {
        var control = new ImageViewportControl { Width = 900, Height = 600 };
        control.Measure(new Size(900, 600)); control.Arrange(new Rect(0, 0, 900, 600)); control.UpdateLayout();
        Assert.True(control.ViewportWidth > 0); return control;
    }
    private static ImageFrame BuildHighFrequency(ImageFrameBuilder builder)
    {
        for (var y = 4800; y < 5200; y++)
        {
            var row = builder.GetRowSpan(y);
            for (var x = 3900; x < 4300; x++) row[x] = (byte)(((x + y) & 1) * 255);
        }
        return builder.Build();
    }
    private static byte[] Copy(ImagePreview preview)
    {
        var stride = (preview.Bitmap.PixelWidth * preview.Bitmap.Format.BitsPerPixel + 7) / 8;
        var bytes = new byte[stride * preview.Bitmap.PixelHeight]; preview.Bitmap.CopyPixels(bytes, stride, 0); return bytes;
    }
    private static ImageFrame Fixture(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(40, 36, format, coordinateSpace: new("fixture", 50, 75));
        for (var y = 0; y < 36; y++)
        {
            var row = builder.GetRowSpan(y);
            for (var x = 0; x < 40; x++)
            {
                var r = (byte)(90 + (3 * x + y) % 100); var g = (byte)(65 + (x + 2 * y) % 120); var b = (byte)(20 + (x + y) % 120);
                if (format == PixelFormat.Mono8) row[x] = (byte)((x + 7 * y) % 256);
                else if (format == PixelFormat.RGB8) { row[x * 3] = r; row[x * 3 + 1] = g; row[x * 3 + 2] = b; }
                else if (format == PixelFormat.BGR8) { row[x * 3] = b; row[x * 3 + 1] = g; row[x * 3 + 2] = r; }
                else if (format == PixelFormat.BayerRG8) row[x] = (y % 2, x % 2) switch { (0, 0) => r, (1, 1) => b, _ => g };
                else row[x] = (y % 2, x % 2) switch { (0, 1) => b, (1, 0) => r, _ => g };
            }
        }
        return builder.Build();
    }
    private sealed class DelayedTileProvider : IImageTileProvider
    {
        private readonly TaskCompletionSource<ImagePreview> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ImagePreview> RequestTileAsync(ImageFrame source, RectRoi imageRegion, int pyramidLevel, CancellationToken cancellationToken) => _completion.Task;
        public void Complete()
        {
            var pixels = new byte[512 * 512]; var bitmap = BitmapSource.Create(512, 512, 96, 96, PixelFormats.Gray8, null, pixels, 512); bitmap.Freeze();
            _completion.SetResult(new(bitmap, pixels.Length, 512, 512));
        }
    }
    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            })); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Editor.Wpf.ROI;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class ImageViewportTests
{
    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    public async Task PreviewUsesCorrectPhysicalChannelsWithoutChangingOriginal(PixelFormat format)
    {
        using var source = CreateColorFixture(format);
        using var input = source.Lease(); var before = input.GetRowSpan(8).ToArray();
        var preview = await new OpenCvPreviewProvider().CreateAsync(source, 2048, 16 * 1024 * 1024, default);
        Assert.True(preview.Bitmap.IsFrozen); Assert.Equal(16, preview.Bitmap.PixelWidth);
        var channels = format == PixelFormat.Mono8 ? 1 : 3;
        var pixels = new byte[16 * 16 * channels]; preview.Bitmap.CopyPixels(pixels, 16 * channels, 0);
        var offset = (8 * 16 + 8) * channels;
        if (channels == 1) Assert.Equal(101, pixels[offset]);
        else { Assert.Equal(30, pixels[offset]); Assert.Equal(100, pixels[offset + 1]); Assert.Equal(220, pixels[offset + 2]); }
        Assert.True(before.AsSpan().SequenceEqual(input.GetRowSpan(8)));
    }

    private static ImageFrame CreateColorFixture(PixelFormat format)
    {
        using var builder = new ImageFrameBuilder(16, 16, format);
        for (var y = 0; y < 16; y++)
        {
            var row = builder.GetRowSpan(y);
            for (var x = 0; x < 16; x++)
            {
                if (format == PixelFormat.Mono8) row[x] = 101;
                else if (format == PixelFormat.RGB8) { row[x * 3] = 220; row[x * 3 + 1] = 100; row[x * 3 + 2] = 30; }
                else if (format == PixelFormat.BGR8) { row[x * 3] = 30; row[x * 3 + 1] = 100; row[x * 3 + 2] = 220; }
                else if (format == PixelFormat.BayerRG8) row[x] = (y % 2, x % 2) switch { (0, 0) => 220, (1, 1) => 30, _ => 100 };
                else row[x] = (y % 2, x % 2) switch { (0, 1) => 30, (1, 0) => 220, _ => 100 };
            }
        }
        return builder.Build();
    }

    [Fact]
    public async Task LruCacheEvictsToItsByteBudgetAndOwnsNoPixelFrames()
    {
        var initial = FrameHandle.ActiveBytes;
        using var cache = new PreviewCache(512 * 1024);
        for (var i = 0; i < 4; i++)
        {
            using (var builder = new ImageFrameBuilder(600, 600, PixelFormat.BGR8))
            using (var source = builder.Build())
            {
                var preview = await cache.GetAsync(source, default);
                Assert.InRange(preview.Bytes, 1, cache.MaximumBytes);
                Assert.InRange(cache.Bytes, 1, cache.MaximumBytes);
                Assert.Equal(1, cache.Count);
            }
            Assert.Equal(initial, FrameHandle.ActiveBytes);
        }
        cache.Clear(); Assert.Equal(0, cache.Bytes); Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task LargeImagePreviewKeepsOneReadonlyShareAndReleasesItOnClose()
    {
        await RunSta(async () =>
        {
            var initial = FrameHandle.ActiveBytes;
            using var control = new ImageViewportControl();
            control.Measure(new Size(1000, 700)); control.Arrange(new Rect(0, 0, 1000, 700));
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.Mono8))
            {
                builder.GetRowSpan(5000).Slice(4090, 10).Fill(220);
                var source = builder.Build(); control.SourceImage = source; source.Dispose();
                await control.WaitForPreviewAsync();
                Assert.Equal(initial + 8192L * 10000, FrameHandle.ActiveBytes);
                Assert.InRange(control.PreviewWidth, 1, 2048); Assert.InRange(control.PreviewHeight, 1, 2048);
                Assert.InRange(control.PreviewBytes, 1, control.MaxPreviewBytes);
                var point = new Point2D(4094.5, 5000.25);
                var screen = control.ImageToScreen(point); var recovered = control.ScreenToImage(screen);
                Assert.Equal(point.X, recovered.X, 8); Assert.Equal(point.Y, recovered.Y, 8);
                control.ShowOneToOne(); Assert.Equal(1, control.ViewTransform.Zoom);
            }
            control.Dispose(); await control.DrainAsync(); Assert.Equal(initial, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public async Task RapidImageChangesNeverPublishCancelledPreviewAndDisposeOnlyOwnedShares()
    {
        await RunSta(async () =>
        {
            var initial = FrameHandle.ActiveBytes;
            using var control = new ImageViewportControl();
            using var firstBuilder = new ImageFrameBuilder(8192, 10000, PixelFormat.BayerRG8);
            using var first = firstBuilder.Build();
            using var secondBuilder = new ImageFrameBuilder(80, 50, PixelFormat.Mono8);
            using var second = secondBuilder.Build();
            control.SourceImage = first; var pending = control.WaitForPreviewAsync();
            control.SourceImage = second; await control.WaitForPreviewAsync(); await pending;
            Assert.Equal(80, control.PreviewWidth); Assert.Equal(50, control.PreviewHeight);
            control.Dispose(); await control.DrainAsync();
            Assert.False(first.IsDisposed); Assert.False(second.IsDisposed);
            first.Dispose(); second.Dispose(); Assert.Equal(initial, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public async Task ThinImageCannotExceedSmallDiscretePreviewBudget()
    {
        using var builder = new ImageFrameBuilder(4096, 1, PixelFormat.BGR8);
        builder.GetRowSpan(0).Fill(100);
        using var source = builder.Build();
        var preview = await new OpenCvPreviewProvider().CreateAsync(source, 2048, 4, default);
        Assert.Equal(1, preview.Bitmap.PixelWidth); Assert.Equal(1, preview.Bitmap.PixelHeight);
        Assert.InRange(preview.Bytes, 1, 4);
    }

    [Fact]
    public async Task DisposeDrainsEveryReplacedPreviewEvenWhenCurrentSourceWasCleared()
    {
        await RunSta(async () =>
        {
            var initial = FrameHandle.ActiveBytes;
            var control = new ImageViewportControl();
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.BayerGB8))
            {
                var source = builder.Build();
                control.SourceImage = source;
                using var view = source.CreateView(1, 1, 8000, 9000);
                control.SourceImage = view;
                control.SourceImage = source;
                source.Dispose();
                control.SourceImage = null;
            }
            control.Dispose();
            await control.DrainAsync();
            Assert.False(control.IsPreviewLoading);
            Assert.Equal(0, control.PreviewBytes);
            Assert.Equal(initial, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public async Task LargeBayerPreviewYieldsToDispatcherAndAllowsDisplayInteraction()
    {
        await RunSta(async () =>
        {
            var initial = FrameHandle.ActiveBytes;
            var control = new ImageViewportControl();
            using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.BayerRG8))
            using (var source = builder.Build())
            {
                var watch = Stopwatch.StartNew();
                control.SourceImage = source;
                var assignmentMs = watch.Elapsed.TotalMilliseconds;
                var heartbeat = false;
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Send);
                Assert.True(heartbeat);
                // The source setter only schedules work; this bound catches a blocking full-frame demosaic.
                Assert.InRange(assignmentMs, 0, 250);
                control.ShowOneToOne(); control.ViewTransform.Pan(10, -12);
                var pixel = new Point2D(4096, 5000);
                var display = control.ImageToScreen(pixel);
                Assert.Equal(pixel.X, control.ScreenToImage(display).X, 8);
                Assert.Equal(pixel.Y, control.ScreenToImage(display).Y, 8);
                await control.WaitForPreviewAsync();
                Assert.InRange(control.PreviewBytes, 1, control.MaxPreviewBytes);
                Assert.InRange(control.PreviewHeight, 1, 2048);
            }
            control.Dispose(); await control.DrainAsync();
            Assert.Equal(initial, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void OriginalCoordinateTransformAndRoiManipulationAreIndependentFromDisplayZoom()
    {
        var transform = new ImageViewTransform(); transform.SetImage(8192, 10000, 50, 75); transform.Fit(900, 700);
        var coordinate = new Point2D(4000.25, 5000.75); var focus = transform.ToScreen(coordinate);
        transform.ZoomAt(focus, 12);
        Assert.Equal(coordinate.X, transform.ToImage(focus).X, 8); Assert.Equal(coordinate.Y, transform.ToImage(focus).Y, 8);
        transform.Pan(37, -18); var screen = transform.ToScreen(coordinate);
        Assert.Equal(coordinate.X, transform.ToImage(screen).X, 8); Assert.Equal(coordinate.Y, transform.ToImage(screen).Y, 8);
        var roi = new RotatedRectRoi(200, 180, 80, 40, 30);
        var moved = RoiInteractionGeometry.Move(roi, 20, -10);
        Assert.Equal(220, moved.CenterX); Assert.Equal(170, moved.CenterY); Assert.Equal(30, moved.AngleDegrees);
        var resized = RoiInteractionGeometry.ResizeCorner(roi, 2, roi.Corners()[2]);
        Assert.Equal(roi.CenterX, resized.CenterX, 8); Assert.Equal(roi.Width, resized.Width, 8);
        var rotated = RoiInteractionGeometry.RotateTo(roi, new(roi.CenterX + 100, roi.CenterY));
        Assert.Equal(90, rotated.AngleDegrees);
        Assert.True(RoiInteractionGeometry.IsInside(rotated, 8192, 10000, new()));
        Assert.False(RoiInteractionGeometry.IsInside(roi with { CenterX = -10 }, 8192, 10000, new()));
    }

    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

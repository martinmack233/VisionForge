using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Wpf.Imaging;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase3CPreviewConcurrencyTests
{
    [Fact]
    public async Task CancelledQueuedLargePreviewsReleaseSharesAndDoNotPreventNextPreview()
    {
        var baseline = FrameHandle.ActiveBytes;
        var provider = new OpenCvPreviewProvider();
        var tasks = new List<Task<ImagePreview>>();
        using var cancellation = new CancellationTokenSource();
        using (var builder = new ImageFrameBuilder(8192, 10000, PixelFormat.BayerRG8))
        using (var source = builder.Build())
        {
            // The full-size Bayer conversion occupies the provider while the following
            // requests queue. Cancel immediately, without depending on a timed sleep.
            tasks.Add(provider.CreateAsync(source, 2048, 16 * 1024 * 1024, default));
            for (var i = 0; i < 8; i++)
                tasks.Add(provider.CreateAsync(source, 2048, 16 * 1024 * 1024, cancellation.Token));
            cancellation.Cancel();
            tasks.Add(provider.CreateAsync(source, 256, 1024 * 1024, default));
            // Providers must retain their own shares, including while waiting for a worker.
            source.Dispose();
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { }
            Assert.True(tasks[0].IsCompletedSuccessfully);
            foreach (var pending in tasks.Skip(1).Take(8)) Assert.True(pending.IsCanceled);
            Assert.True(tasks[^1].IsCompletedSuccessfully);
            var first = await tasks[0];
            var last = await tasks[^1];
            Assert.True(first.Bitmap.IsFrozen);
            Assert.Equal(8192, first.SourceWidth);
            Assert.Equal(10000, first.SourceHeight);
            Assert.InRange(last.Bitmap.PixelWidth, 1, 256);
            Assert.InRange(last.Bitmap.PixelHeight, 1, 256);
            var pixels = new byte[3];
            last.Bitmap.CopyPixels(new System.Windows.Int32Rect(10, 10, 1, 1), pixels, 3, 0);
            Assert.Equal(new byte[] { 0, 0, 0 }, pixels);
        }
        // Frozen preview bitmaps can remain live; they must not retain native source buffers.
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
        GC.KeepAlive(tasks);
    }
}

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Devices;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using Xunit;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase4CDevicesTests
{
    [Fact]
    public void RealDevicePageCapturesDisplaysStopsRestartsAndReleasesOnlyItsOwnReferences()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession();
            using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root, FrameRateText = "60" };
            var page = Page(vm);
            try
            {
                await vm.InitializeAsync(); Assert.Contains(vm.Devices, d => d.IsVirtual && d.ProviderId == "virtual-folder");
                await vm.OpenAsync(); Assert.Equal("Open", vm.DeviceState); Assert.Null(vm.PreviewImage); Assert.Equal(baseline, FrameHandle.ActiveBytes);
                Assert.DoesNotContain(vm.Parameters, p => p.Name.Contains("Exposure", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Gain", StringComparison.OrdinalIgnoreCase));
                Assert.False(vm.Parameters.Single(p => p.Name == "Width").CanWrite);
                await vm.CaptureAsync(); Assert.Equal("", vm.Error); Assert.NotNull(vm.PreviewImage);
                Assert.Equal("a.bmp", vm.FrameInfo!.SourceName); Assert.Equal(1, vm.FrameInfo.Sequence);
                await page.PreviewViewport.WaitForDisplayAsync(); Assert.True(page.PreviewViewport.PreviewWidth > 0);
                using (var borrowed = vm.PreviewImage!.Share())
                {
                    await vm.StopAsync(); await page.PreviewViewport.DrainAsync();
                    Assert.Null(vm.PreviewImage); Assert.False(borrowed.IsDisposed);
                    using var pixels = borrowed.Lease(); Assert.Equal(20, pixels.GetRowSpan(0)[0]);
                }
                await vm.StartAsync(); await Until(() => vm.FramesReceived >= 5);
                var previousId = vm.FrameInfo!.FrameId; Assert.True(vm.IsAcquiring);
                await vm.StopAsync(); await page.PreviewViewport.DrainAsync();
                Assert.False(vm.IsAcquiring); Assert.Null(vm.PreviewImage); Assert.Equal("Open", vm.DeviceState);
                Assert.Equal(baseline, FrameHandle.ActiveBytes);
                await vm.CaptureAsync(); Assert.NotEqual(previousId, vm.FrameInfo!.FrameId);
                await vm.CloseAsync(); await page.PreviewViewport.DrainAsync();
                Assert.Equal("Closed", vm.DeviceState); Assert.Empty(vm.Parameters); Assert.Equal(baseline, FrameHandle.ActiveBytes);
            }
            finally { await vm.StopAsync(); await page.CloseAsync(); await session.CloseAsync(); }
            Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void ProjectSwitchCancelsActivePreviewAndRejectsLateFramesFromOldCameraSession()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root, FrameRateText = "120" };
            var page = Page(vm);
            try
            {
                var oldManager = session.CameraSession;
                await vm.StartAsync(); await Until(() => vm.FramesReceived >= 2);
                var revision = session.AcquisitionRevision;
                await session.NewAsync(); Assert.True(session.AcquisitionRevision > revision);
                Assert.Null(vm.PreviewImage); Assert.False(vm.IsAcquiring); Assert.Empty(vm.Parameters);
                await vm.StopAsync(); await page.PreviewViewport.DrainAsync(); await Task.Delay(60);
                Assert.Null(vm.PreviewImage); Assert.Equal(0, vm.FramesReceived); Assert.NotSame(oldManager, session.CameraSession);
                Assert.Equal(baseline, FrameHandle.ActiveBytes);
                await vm.CaptureAsync(); Assert.Equal(1, vm.FrameInfo!.Sequence); Assert.Equal("a.bmp", vm.FrameInfo.SourceName);
            }
            finally { await vm.StopAsync(); await page.CloseAsync(); await session.CloseAsync(); }
            Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void PreviewBackpressureDoesNotQueueFramesAndStopIsReusable()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root, FrameRateText = "1000" };
            var observePlateau = false;
            var plateau = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePlateau = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.WaitForPreviewAsync = async () =>
            {
                if (observePlateau) { plateau.TrySetResult(); await releasePlateau.Task; }
                else await Task.Delay(50);
            };
            await vm.StartAsync(); await Until(() => vm.FramesReceived >= 3); var received = vm.FramesReceived;
            await Task.Delay(150); Assert.InRange(vm.FramesReceived - received, 1, 5);
            // Observe the retained preview at the actual consumer backpressure barrier.
            // A timer alone can resume between worker decode and dispatcher handoff,
            // when the next in-flight frame legitimately coexists with the last preview.
            observePlateau = true; await plateau.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { Assert.Equal(48 * 32, FrameHandle.ActiveBytes - baseline); }
            finally { releasePlateau.TrySetResult(); }
            await vm.StopAsync(); Assert.Null(vm.PreviewImage); Assert.Equal(baseline, FrameHandle.ActiveBytes);
            await vm.StartAsync(); await Until(() => vm.PreviewImage is not null); await vm.StopAsync();
            await session.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void StopCancelsAnObsoletePreviewWaitWithoutBlockingTheUi()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root, FrameRateText = "120" };
            var previewStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var obsoletePreview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.WaitForPreviewAsync = () => { previewStarted.TrySetResult(); return obsoletePreview.Task; };
            await vm.StartAsync(); await previewStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2)); Assert.Null(vm.PreviewImage); Assert.False(vm.IsAcquiring);
            Assert.Equal(baseline, FrameHandle.ActiveBytes);
            obsoletePreview.TrySetResult(); await session.CloseAsync();
        });
    }

    [Fact]
    public void DeviceParametersUseRealCapabilitiesTypedValuesAndNeverWriteReadonlyFields()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); using var session = new EditorSession();
            using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root };
            await vm.OpenAsync(); var rate = vm.Parameters.Single(p => p.Name == "FrameRate"); Assert.True(rate.CanWrite);
            rate.ValueText = "27.5"; await vm.ApplyParameterAsync(rate); Assert.Equal("", vm.Error); Assert.Equal("27.5", vm.FrameRateText);
            var actual = await (await session.CameraSession!.GetVirtualDeviceAsync("virtual-main", new() { FolderPath = files.Root, FrameRate = 27.5 })).ReadParametersAsync();
            Assert.Equal(27.5, actual["FrameRate"].GetDouble());
            var width = vm.Parameters.Single(p => p.Name == "Width"); width.ValueText = "999";
            await vm.ApplyParameterAsync(width); Assert.Contains("不可写", vm.Error); Assert.Equal(0, (await (await session.CameraSession.GetVirtualDeviceAsync("virtual-main", new() { FolderPath = files.Root, FrameRate = 27.5 })).ReadParametersAsync())["Width"].GetInt32());
            rate = vm.Parameters.Single(p => p.Name == "FrameRate"); rate.ValueText = "NaN";
            await vm.ApplyParameterAsync(rate); Assert.Contains("有限", vm.Error);
            await vm.StartAsync(); await Until(() => vm.PreviewImage is not null);
            Assert.All(vm.Parameters.Where(p => p.Capability.RequiresStopped), p => Assert.False(p.CanWrite));
            await vm.StopAsync(); Assert.All(vm.Parameters.Where(p => p.Capability.Writable), p => Assert.True(p.CanWrite));
            await vm.CloseAsync(); await session.CloseAsync();
        });
    }

    [Fact]
    public void RealRuntimeUsesNextCameraFrameRatherThanThePreviousPreviewAndConfigurationSurvivesReload()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession();
            using var vm = new DevicesViewModel(session, (id, options) =>
            {
                session.AddNode("VirtualCamera"); var camera = session.SelectedNode!;
                session.CommitParameters(camera, JsonDefaults.Element(new VirtualCameraParameters { CameraId = id, FolderPath = options.FolderPath, FrameRate = options.FrameRate, Loop = options.Loop, PixelFormat = options.PixelFormat }));
                session.AddNode("ImageInput"); var input = session.SelectedNode!;
                session.AddNode("GaussianBlur"); var blur = session.SelectedNode!;
                Assert.True(session.TryConnect(camera.Outputs.Single(p => p.Name == "Image"), input.Inputs.Single(p => p.Name == "Image"), out _));
                Assert.True(session.TryConnect(input.Outputs.Single(p => p.Name == "Image"), blur.Inputs.Single(p => p.Name == "Image"), out _));
            }) { FolderPath = files.Root, FrameRateText = "10" };
            await vm.ConfigureNodeAsync(); Assert.Equal("", vm.Error); Assert.Equal(3, session.Nodes.Count);
            await vm.CaptureAsync(); Assert.Equal("a.bmp", vm.FrameInfo!.SourceName); var previewId = vm.FrameInfo.FrameId;
            await vm.RunProjectAsync(); Assert.Equal("", vm.Error); Assert.Null(vm.PreviewImage); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
            var cameraId = session.Nodes.Single(n => n.TypeId == "VirtualCamera").NodeId;
            var frameInfo = Assert.IsType<CameraFrameInfo>(session.CurrentOutputs[cameraId]["FrameInfo"]);
            Assert.Equal("b.bmp", frameInfo.SourceName); Assert.Equal(2, frameInfo.Sequence); Assert.NotEqual(previewId, frameInfo.FrameId);
            var blurId = session.Nodes.Single(n => n.TypeId == "GaussianBlur").NodeId;
            var filtered = Assert.IsType<ImageFrame>(session.CurrentOutputs[blurId]["ProcessedImage"]); using (var pixels = filtered.Lease()) Assert.InRange(pixels.GetRowSpan(0)[0], (byte)1, (byte)199);
            var path = Path.Combine(files.Root, "project.json"); await session.SaveAsync(path); await session.OpenAsync(path);
            Assert.Equal("virtual-main", vm.CameraId); Assert.Equal(files.Root, Path.GetFullPath(vm.FolderPath, session.ResourceRoot)); Assert.Equal("10", vm.FrameRateText); Assert.Null(vm.PreviewImage);
            await vm.RunProjectAsync(); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
            await vm.StopAsync(); await session.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Theory]
    [InlineData(PixelFormat.Mono8)]
    [InlineData(PixelFormat.BayerRG8)]
    [InlineData(PixelFormat.BayerGB8)]
    [InlineData(PixelFormat.RGB8)]
    [InlineData(PixelFormat.BGR8)]
    public void DevicePagePreservesEachSupportedRawFormat(PixelFormat format)
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(format); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root, PixelFormatChoice = format.ToString() };
            var page = Page(vm);
            try
            {
                await vm.CaptureAsync(); Assert.Equal("", vm.Error); Assert.Equal(format, vm.PreviewImage!.PixelFormat);
                var before = Pixels(vm.PreviewImage); await page.PreviewViewport.WaitForDisplayAsync(); Assert.Equal(before, Pixels(vm.PreviewImage));
                Assert.True(page.PreviewViewport.PreviewWidth > 0); await vm.CloseAsync(); await page.PreviewViewport.DrainAsync();
            }
            finally { await vm.StopAsync(); await page.CloseAsync(); await session.CloseAsync(); }
            Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void InvalidCameraConfigurationGivesClearErrorAndNoPreviousFrame()
    {
        RunSta(async dispatcher =>
        {
            using var files = new Fixtures(); var baseline = FrameHandle.ActiveBytes;
            using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Root };
            await vm.CaptureAsync(); Assert.NotNull(vm.PreviewImage);
            await vm.CloseAsync(); vm.FolderPath = Path.Combine(files.Root, "missing"); await vm.CaptureAsync();
            Assert.NotEmpty(vm.Error); Assert.Null(vm.PreviewImage);
            vm.FolderPath = files.Root; vm.FrameRateText = "NaN"; await vm.OpenAsync(); Assert.Contains("帧率", vm.Error);
            vm.FrameRateText = "5"; vm.PixelFormatChoice = "Mono16"; await vm.CaptureAsync(); Assert.Contains("像素格式", vm.Error); Assert.Null(vm.PreviewImage);
            await vm.StopAsync(); await session.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
        });
    }

    [Fact]
    public void ParameterEditorsKeepIntegerNumberBooleanEnumTypesAndReadonlyCapability()
    {
        var i = new CameraParameterEditor(new("I", "整数", CameraParameterKind.Integer, true, true), JsonDefaults.Element(2)); i.ValueText = "42";
        Assert.Equal(JsonValueKind.Number, i.ToJson().ValueKind); Assert.Equal(42, i.ToJson().GetInt32()); i.ValueText = "1.5"; Assert.Throws<ArgumentException>(() => i.ToJson());
        var n = new CameraParameterEditor(new("N", "数值", CameraParameterKind.Number, true, true), JsonDefaults.Element(.5)); n.ValueText = "1.25"; Assert.Equal(1.25, n.ToJson().GetDouble());
        var b = new CameraParameterEditor(new("B", "布尔", CameraParameterKind.Boolean, true, true), JsonDefaults.Element(false)); b.BooleanValue = true; Assert.True(b.ToJson().GetBoolean()); Assert.Equal(Visibility.Visible, b.BooleanVisibility);
        var e = new CameraParameterEditor(new("E", "枚举", CameraParameterKind.Enumeration, true, true, AllowedValues: ["A", "B"]), JsonDefaults.Element("A")); e.ValueText = "C"; Assert.Throws<ArgumentException>(() => e.ToJson()); e.ValueText = "B"; Assert.Equal("B", e.ToJson().GetString());
        var readOnly = new CameraParameterEditor(new("R", "宽度", CameraParameterKind.Integer, true, false), JsonDefaults.Element(32)); readOnly.SetAvailability(true, false); Assert.False(readOnly.CanWrite);
    }

    private static DevicesPage Page(DevicesViewModel vm)
    {
        var page = new DevicesPage { DataContext = vm, Width = 1100, Height = 750 };
        page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        page.Measure(new Size(1100, 750)); page.Arrange(new Rect(0, 0, 1100, 750)); page.UpdateLayout(); return page;
    }
    private static byte[] Pixels(ImageFrame image)
    {
        using var lease = image.Lease(); var result = new byte[image.Width * image.Height * image.PixelFormat.BytesPerPixel()];
        for (var y = 0; y < image.Height; y++) lease.GetRowSpan(y).CopyTo(result.AsSpan(y * lease.RowBytes, lease.RowBytes)); return result;
    }
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { Assert.True(DateTime.UtcNow < timeout, "Timed out waiting for actual device frames."); await Task.Delay(10); }
    }
    private static void RunSta(Func<Dispatcher, Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(dispatcher); } catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            })); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Fixtures : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "VisionStudio-Devices-Test-" + Guid.NewGuid().ToString("N"));
        public Fixtures(PixelFormat format = PixelFormat.Mono8)
        {
            Directory.CreateDirectory(Root);
            foreach (var (name, value) in new[] { ("a.bmp", (byte)20), ("b.bmp", (byte)200) })
            {
                using var builder = new ImageFrameBuilder(48, 32, format);
                builder.GetRowSpan(0)[0] = value;
                using var image = builder.Build(); ImageFile.Save(image, Path.Combine(Root, name));
            }
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root); var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("VisionStudio-Devices-Test-", StringComparison.Ordinal) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}

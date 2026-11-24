using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Devices;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase5DevicesTests
{
    [Fact]
    public void FrozenAndFreshDetectionUseDifferentExplicitFramesAndReleaseResources() => Sta(async () =>
    {
        var baseline = FrameHandle.ActiveBytes; using var files = new Files(); using var session = new EditorSession();
        Bind(session, files.Path);
        using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Path };
        await vm.OpenAsync(); await vm.CaptureAsync(); var frozen = vm.FrameInfo!;
        await vm.FreezePreviewAsync(); Assert.True(vm.IsFrozen); Assert.NotNull(vm.PreviewImage);
        await vm.DetectFrozenAsync(); Assert.Equal("", vm.Error);
        Assert.Equal(frozen.FrameId, vm.LastDetectionFrameId);
        var captureId = session.Nodes.Single(n => n.TypeId == "VirtualCamera").NodeId;
        Assert.Equal(frozen.FrameId, ((ImageFrame)session.CurrentOutputs[captureId]["Image"]).FrameId);
        await vm.RunProjectAsync(); Assert.Equal("", vm.Error);
        Assert.NotEqual(frozen.FrameId, ((ImageFrame)session.CurrentOutputs[captureId]["Image"]).FrameId);
        await vm.CloseAsync(); await session.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
    });

    [Fact]
    public void ContinuousDetectionHasOneWaitingFrameDropsOldFramesAndCancelsCleanly() => Sta(async () =>
    {
        var baseline = FrameHandle.ActiveBytes; using var files = new Files(); using var session = new EditorSession();
        session.Registry.Register("TestDelay", () => new DelayNode()); session.Catalog.Register<DelayParameters>("TestDelay", "验收等待替身", "测试");
        Bind(session, files.Path); session.AddNode("TestDelay"); var slow = session.SelectedNode!;
        Assert.True(session.TryConnect(session.Nodes.Single(n => n.TypeId == "VirtualCamera").Outputs[0], slow.Inputs[0], out _));
        using var vm = new DevicesViewModel(session, (_, _) => { }) { FolderPath = files.Path, FrameRateText = "1000" };
        await vm.OpenAsync(); await vm.StartContinuousDetectionAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (vm.DetectedFrames < 2 && watch.Elapsed < TimeSpan.FromSeconds(5)) { Assert.InRange(vm.QueuedFrames, 0, 1); await Task.Delay(15); }
        Assert.True(vm.DetectedFrames >= 2); Assert.True(vm.DroppedDetectionFrames > 0);
        await vm.FreezePreviewAsync().WaitAsync(TimeSpan.FromSeconds(3)); Assert.False(vm.IsAcquiring); Assert.False(vm.IsDetecting); Assert.Equal(0, vm.QueuedFrames);
        Assert.True(vm.IsFrozen); await vm.CloseAsync(); await session.CloseAsync(); Assert.Equal(baseline, FrameHandle.ActiveBytes);
    });

    [Fact]
    public void InvalidDraftPreventsButtonsAndSdkAbsenceKeepsVirtualProviderUsable() => Sta(async () =>
    {
        using var session = new EditorSession(); using var vm = new DevicesViewModel(session, (_, _) => { });
        Assert.False(vm.OpenCommand.CanExecute(null)); Assert.NotEmpty(vm.ConfigurationError);
        await vm.InitializeAsync(); Assert.Contains(vm.Devices, d => d.ProviderId == "virtual-folder");
        vm.SelectedProviderId = "boq"; Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.NotEmpty(vm.ProviderDiagnostic); Assert.False(vm.CaptureCommand.CanExecute(null));
        vm.SelectedProviderId = "virtual-folder"; using var files = new Files(); vm.FolderPath = files.Path;
        Assert.True(vm.OpenCommand.CanExecute(null)); await vm.OpenAsync(); Assert.True(vm.CaptureCommand.CanExecute(null));
        await vm.CloseAsync(); await session.CloseAsync();
    });

    private static void Bind(EditorSession session, string path)
    {
        session.AddNode("VirtualCamera"); var camera = session.SelectedNode!;
        session.CommitParameters(camera, JsonDefaults.Element(new VirtualCameraParameters { FolderPath = path, FrameRate = 1000 }));
        session.AddNode("ImageDisplay");
        Assert.True(session.TryConnect(camera.Outputs[0], session.SelectedNode!.Inputs.Single(p => p.Name == "Image"), out _));
    }
    public sealed class DelayParameters { }
    private sealed class DelayNode : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new("TestDelay", "1.0.0", [new("Image", typeof(ImageFrame))], []);
        public IReadOnlyList<string> ValidateParameters(JsonElement p) => [];
        public async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct) { await Task.Delay(90, ct); return new(new Dictionary<string, object>()); }
    }
    private static void Sta(Func<Task> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await body(); } catch (Exception e) { failure = e; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(35)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Files : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisionStudio-Phase5-Camera-" + Guid.NewGuid().ToString("N"));
        public Files() { Directory.CreateDirectory(Path); using var builder = new ImageFrameBuilder(32, 24, PixelFormat.Mono8); builder.GetRowSpan(2)[4] = 255; using var frame = builder.Build(); ImageFile.Save(frame, System.IO.Path.Combine(Path, "frame.bmp")); }
        public void Dispose() { if (Path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(Path).StartsWith("VisionStudio-Phase5-Camera-")) Directory.Delete(Path, true); }
    }
}

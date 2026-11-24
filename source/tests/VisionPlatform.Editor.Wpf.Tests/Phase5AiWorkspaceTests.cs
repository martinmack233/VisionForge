using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.AI;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase5AiWorkspaceTests
{
    [Fact]
    public void AllSixPagesRenderWithoutStartingPythonOrMutatingInspection() => Sta(async () =>
    {
        using var session = new EditorSession(); var client = new IdleClient();
        var page = new AiWorkspacePage(session, client, new());
        var window = Window(page);
        try
        {
            window.Show(); await page.InitializeAsync();
            var tabs = Assert.IsType<TabControl>(page.FindName("AiTabs")); Assert.Equal(6, tabs.Items.Count);
            foreach (var tab in tabs.Items.Cast<TabItem>()) { tabs.SelectedItem = tab; window.UpdateLayout(); await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
            Assert.False(client.IsRunning); Assert.Equal(0, client.Calls); Assert.Empty(session.Nodes); Assert.False(session.HasChanges);
            Assert.True(await page.PrepareCloseAsync(_ => Task.FromResult(AiCloseChoice.Cancel)));
        }
        finally { await page.DisposeAsync(); window.Close(); await session.CloseAsync(); }
    });

    [Fact]
    public void RealPythonServiceImportsAnnotatesUndoReloadAndReleasesSeventyNativeTiles() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var session = new EditorSession(); await using var client = new AiWorkspaceClient();
        var page = new AiWorkspacePage(session, client, fixture.Settings); var vm = page.ViewModel; var window = Window(page);
        try
        {
            window.Show(); await page.InitializeAsync(); await vm.ConnectAsync(); Assert.True(vm.IsConnected);
            vm.NewProjectName = "WPF独立服务验收"; await vm.CreateProjectAsync(); await Until(() => vm.Classes.Count > 0 && vm.HasProject);
            await vm.ImportAsync([fixture.ImagePath], false); await UntilAsync(async () => { await vm.PollAsync(); return vm.Images.Count == 1 && vm.Tasks.All(t => !t.Active); });
            vm.SelectedImage = vm.Images[0]; await Until(() => vm.ImageWidth == 64 && vm.PreviewPath.Length > 0);
            Assert.Equal("", vm.Error); Assert.False(vm.SelectedImage.Reviewed);
            var box = new AiBox { ClassId = vm.Classes[0].Id, X = 8, Y = 6, Width = 16, Height = 12 };
            vm.BeginAnnotationEdit(); vm.Boxes.Add(box); vm.AnnotationEdited(); await vm.FlushAnnotationsAsync();
            var saved = await client.CallAsync("annotations.get", new { project_id = vm.SelectedProject!.Id, image_id = vm.SelectedImage.Id });
            Assert.Single(saved.GetProperty("boxes").EnumerateArray()); Assert.True(saved.GetProperty("reviewed").GetBoolean());
            vm.UndoAnnotation(); await vm.FlushAnnotationsAsync();
            var undone = await client.CallAsync("annotations.get", new { project_id = vm.SelectedProject.Id, image_id = vm.SelectedImage.Id }); Assert.Empty(undone.GetProperty("boxes").EnumerateArray());
            vm.BeginAnnotationEdit(); vm.Boxes.Add(box.Copy()); vm.AnnotationEdited(); await vm.FlushAnnotationsAsync();
            var canvas = Assert.IsType<AiAnnotationCanvas>(page.FindName("AnnotationCanvas"));
            for (var n = 0; n < 70; n++) await vm.RequestNativeTileAsync(8, 6, 16, 12);
            Assert.Equal(new Rect(8, 6, 16, 12), canvas.NativeTileRegion); Assert.Equal(16, canvas.NativeTilePixelWidth); Assert.Equal(12, canvas.NativeTilePixelHeight);
            Assert.InRange(Directory.GetFiles(fixture.Settings.TransferRoot, "*.png").Length, 0, 2);
            var original = new Point(12.25, 10.75); canvas.OneToOne(); var restored = canvas.ToOriginal(canvas.ToViewport(original)); Assert.Equal(original.X, restored.X, 7); Assert.Equal(original.Y, restored.Y, 7);
            Assert.Equal(fixture.OriginalHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.ImagePath))));
            Assert.Empty(session.Nodes); Assert.False(session.HasChanges);
            var projectId = vm.SelectedProject.Id;
            await vm.RefreshImagesAsync(); vm.SelectedImage = vm.Images[0]; await Until(() => vm.Boxes.Count == 1 && vm.AnnotationState.Contains("已保存")); Assert.Equal(8, vm.Boxes[0].X);
            Assert.Equal(projectId, vm.SelectedProject.Id);
            Assert.True(await page.PrepareCloseAsync(_ => Task.FromResult(AiCloseChoice.StopTasks)));
        }
        finally { await page.DisposeAsync(); window.Close(); await session.CloseAsync(); }
    });

    [Fact]
    public void RealServiceReviewFilterAndClassStatisticsPreserveNormalVersusUnchecked() => Sta(async () =>
    {
        using var fixture = new Fixture(); using var session = new EditorSession(); await using var client = new AiWorkspaceClient();
        var page = new AiWorkspacePage(session, client, fixture.Settings); var vm = page.ViewModel;
        try
        {
            await vm.ConnectAsync(); vm.NewProjectName = "WPF筛选验收"; await vm.CreateProjectAsync(); await Until(() => vm.Classes.Count > 0);
            await vm.ImportAsync([fixture.ImagePath], false); await UntilAsync(async () => { await vm.PollAsync(); return vm.Images.Count == 1 && vm.Tasks.All(t => !t.Active); });
            vm.ReviewFilter = "正常样本"; await vm.RefreshImagesAsync(); Assert.Empty(vm.Images);
            vm.ReviewFilter = "全部"; await vm.RefreshImagesAsync(); await vm.ReviewAsync([vm.Images[0]], true);
            vm.ReviewFilter = "正常样本"; await vm.RefreshImagesAsync(); Assert.Single(vm.Images); Assert.Equal("已检查 / 正常", vm.Images[0].State);
            vm.ReviewFilter = "未检查"; await vm.RefreshImagesAsync(); Assert.Empty(vm.Images);
        }
        finally { await page.DisposeAsync(); await session.CloseAsync(); }
    });

    private static Window Window(FrameworkElement content)
    {
        var window = new Window { Width = 1280, Height = 800, Content = content, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") }); return window;
    }
    private static async Task Until(Func<bool> condition) => await UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    { var watch = System.Diagnostics.Stopwatch.StartNew(); while (!await condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("AI WPF服务验收等待超时。"); await Task.Delay(40); } }
    private static void Sta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await action(); } catch (Exception ex) { failure = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } })); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(90)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class IdleClient : IAiWorkspaceClient
    {
        public int Calls; public bool IsRunning => false; public AiServiceSettings Settings { get; } = new();
        public event EventHandler<AiServiceEvent>? EventReceived { add { } remove { } }
        public Task StartAsync(AiServiceSettings settings, CancellationToken ct = default) { Calls++; return Task.CompletedTask; }
        public Task<JsonElement> CallAsync(string method, object? payload = null, CancellationToken ct = default) { Calls++; return Task.FromResult(JsonSerializer.SerializeToElement(new { })); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "VisionStudio-AiWpf-" + Guid.NewGuid().ToString("N"));
        public AiServiceSettings Settings { get; }
        public string ImagePath { get; }
        public string OriginalHash { get; }
        public Fixture()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory); while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "VisionPlatform.sln"))) repository = repository.Parent;
            if (repository is null) throw new DirectoryNotFoundException("找不到源码解决方案根目录。");
            var yolo = Path.Combine(repository.Parent!.FullName, "YOLOStudio"); Directory.CreateDirectory(Root);
            Settings = new() { YoloRoot = yolo, PythonPath = Path.Combine(yolo, ".venv", "Scripts", "python.exe"), DataHome = Path.Combine(Root, "data"), TransferRoot = Path.Combine(Root, "transfer"), ServiceScript = Path.Combine(repository.FullName, "tools", "ai_workspace_service", "service.py") };
            ImagePath = Path.Combine(Root, "sample.png"); var pixels = Enumerable.Range(0, 64 * 48).Select(i => (byte)(i % 256)).ToArray();
            var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Gray8, null, pixels, 64); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(ImagePath)) encoder.Save(stream);
            OriginalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ImagePath)));
        }
        public void Dispose()
        { var full = Path.GetFullPath(Root); if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("VisionStudio-AiWpf-")) throw new InvalidOperationException("测试目录保护。"); if (Directory.Exists(full)) Directory.Delete(full, true); }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf.Collections;
using VisionPlatform.Editor.Wpf.Examples;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Editor.Wpf.Parameters;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

[Collection("Calibration workspace")]
public sealed class FilterGeometryWindowTests
{
    [Fact]
    public void ExampleCenterParameterModesSelectionAndOverlayUseTheActualWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await ExerciseAsync(); }
                catch (Exception ex) { failure = ex; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task ExerciseAsync()
    {
        var state = Directory.CreateTempSubdirectory("VisionForge-operators-ui-");
        await new WorkspaceStateStore(state.FullName).SaveAsync(new(1,
            new WorkspaceSettings { DefaultProjectDirectory = Path.Combine(state.FullName, "Projects"), LogDirectory = Path.Combine(state.FullName, "Logs") }, []));
        using var session = new EditorSession();
        var window = new MainWindow(session, state.FullName) { Width = 1280, Height = 820 };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml")
        });
        try
        {
            window.Show();
            await window.WorkspaceReady;
            await OpenExampleAsync("image-filter-methods");
            Assert.True(session.InspectOutput("bilateral", "ProcessedImage"));
            var tabs = (TabControl)window.FindName("InspectorTabs");
            tabs.SelectedIndex = 0;
            await ArrangeAsync(window);
            var parameters = (ParameterPanel)window.FindName("ParametersView");
            var mode = Find<ComboBox>(parameters, "Parameter.Method");
            Assert.True(mode.IsVisible);
            Assert.True(Find<TextBox>(parameters, "Parameter.Diameter").IsVisible);
            Assert.False(Find<TextBox>(parameters, "Parameter.KernelWidth").IsVisible);
            await CaptureAsync(window, "image-filter-parameters");
            mode.SelectedValue = "Directional";
            Assert.False(Find<TextBox>(parameters, "Parameter.Diameter").IsVisible);
            Assert.True(Find<ComboBox>(parameters, "Parameter.SamplingDirection").IsVisible);
            Assert.True(Find<TextBox>(parameters, "Parameter.KernelSize").IsVisible);
            mode.SelectedValue = "Bilateral";
            Find<TextBox>(parameters, "Parameter.SigmaColor").Text = "45";
            Assert.True(parameters.ApplyChanges());
            Assert.Empty(session.CurrentOutputs);
            await session.SaveAsync();
            var filterCopy = session.ProjectPath!;
            await session.OpenAsync(filterCopy);
            Assert.Contains("45", session.Nodes.Single(n => n.NodeId == "bilateral").Parameters.GetRawText());
            await session.RunAsync();
            Assert.True(session.InspectOutput("bilateral", "ProcessedImage"));

            await OpenExampleAsync("contour-envelope-geometry");
            Assert.True(session.InspectOutput("display", "Display"));
            tabs.SelectedIndex = 2;
            await ArrangeAsync(window);
            var table = ((CollectionResultsView)window.FindName("CollectionsView")).ResultTable;
            Assert.Equal(5, table.Items.Count);
            table.SelectedIndex = 1;
            var item = Assert.IsType<ContourEnvelopeItem>(session.CollectionSelection);
            Assert.NotNull(item.Circle);
            var viewport = (ImageViewportControl)window.FindName("Viewport");
            Assert.Same(item, viewport.CollectionSelection);
            await viewport.WaitForDisplayAsync();
            // A completed tile request can still leave the overlay queued for the next WPF render pass.
            await ArrangeAsync(window);
            await CaptureAsync(window, "contour-envelope-selected");
            var rendered = Render(window);
            var circle = item.Circle!;
            var rim = new (double X, double Y)[]
            {
                (circle.Center.X + circle.Radius, circle.Center.Y), (circle.Center.X - circle.Radius, circle.Center.Y),
                (circle.Center.X, circle.Center.Y + circle.Radius), (circle.Center.X, circle.Center.Y - circle.Radius)
            };
            var projected = rim.Select(pixel =>
            {
                var pt = viewport.ImageToScreen(new(pixel.X, pixel.Y));
                pt.Offset(viewport.ViewportBounds.X, viewport.ViewportBounds.Y);
                return viewport.TranslatePoint(pt, window);
            }).ToArray();
            Assert.True(projected.Any(pt => ContainsSelectionYellow(rendered, pt)),
                "未在选中圆的投影边界找到黄色轮廓：" + string.Join(", ", projected));

            Assert.True(session.InspectOutput("inner", "Collection"));
            await ArrangeAsync(window);
            Assert.Equal(7, table.Items.Count);
            Assert.Contains(table.Items.Cast<CollectionTableRow>(), row => row.Item.Fields["Status"].Text == "HoleBoundary");
            Assert.Contains(table.Items.Cast<CollectionTableRow>(), row => row.Item.Fields["Status"].Text == "IncompleteBoundary");
            await CaptureAsync(window, "contour-envelope-status");
        }
        finally
        {
            await window.CloseEditorAsync(true);
            state.Delete(recursive: true);
        }

        async Task OpenExampleAsync(string id)
        {
            window.Navigate(WorkspacePage.Examples);
            await ArrangeAsync(window);
            var page = Descendants(window).OfType<ExamplesPage>().Single();
            var examples = Assert.IsType<ExamplesViewModel>(page.DataContext);
            var list = Find<ListBox>(page, "ExampleList");
            list.SelectedItem = examples.VisibleItems.Single(e => e.Manifest.Id == id);
            var run = Find<Button>(page, "RunExample");
            Assert.True(run.IsVisible && run.IsEnabled);
            run.Command.Execute(run.CommandParameter);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (examples.IsBusy) await Task.Delay(30, timeout.Token);
            Assert.True(examples.LastVerification?.Passed, $"{examples.Status} {examples.LastVerification?.Summary}");
            window.Navigate(WorkspacePage.Editor);
            await ArrangeAsync(window);
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Descendants(root).OfType<T>().Single(el => AutomationProperties.GetAutomationId(el) == id);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static async Task ArrangeAsync(Window window) =>
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);

    private static async Task CaptureAsync(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("VISIONFORGE_OPERATOR_UI_EVIDENCE") is not { Length: > 0 } path) return;
        await ArrangeAsync(window);
        Directory.CreateDirectory(path);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(Render(window)));
        using var stream = File.Create(Path.Combine(path, name + ".png"));
        png.Save(stream);
    }

    private static RenderTargetBitmap Render(FrameworkElement view)
    {
        var picture = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        picture.Render(view);
        return picture;
    }

    private static bool ContainsSelectionYellow(BitmapSource picture, Point location)
    {
        var x = Math.Clamp((int)Math.Round(location.X) - 3, 0, picture.PixelWidth - 7);
        var y = Math.Clamp((int)Math.Round(location.Y) - 3, 0, picture.PixelHeight - 7);
        var pixels = new byte[7 * 7 * 4];
        picture.CopyPixels(new Int32Rect(x, y, 7, 7), pixels, 7 * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > 230 && pixels[i + 1] > 145 && pixels[i] < 80) return true;
        return false;
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf.Imaging;
using System.Windows.Threading;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Workspace;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase5ThemeInteractionTests
{
    [Fact]
    public void ComboPopupAndEditableTextKeepKeyboardSelectionAndCopyPaste()
    {
        RunSta(() =>
        {
            var combo = new ComboBox { Width = 240, IsEditable = false, ItemsSource = new[] { "Mono8", "BayerRG8", "BGR8" }, SelectedIndex = 0 };
            var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(combo);
            var window = CreateWindow(panel);
            IDataObject? previousClipboard = null;
            try
            {
                window.Show(); window.UpdateLayout(); combo.Focus();
                var source = PresentationSource.FromVisual(combo)!;
                combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Down) { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.Equal(1, combo.SelectedIndex);
                combo.IsDropDownOpen = true; Pump();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                Assert.True(popup.IsOpen); Assert.NotNull(popup.Child);
                var first = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.NotNull(first.Template);
                Assert.Equal(ColorConverter.ConvertFromString("#243B53"), ((SolidColorBrush)first.Foreground).Color);
                combo.IsDropDownOpen = false; combo.IsEditable = true; combo.ApplyTemplate(); Pump();
                var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Assert.True(MainWindow.IsTextEditing(editor));
                previousClipboard = Clipboard.GetDataObject();
                editor.Text = "123.45"; editor.SelectAll(); ApplicationCommands.Copy.Execute(null, editor);
                Assert.Equal("123.45", Clipboard.GetText());
                editor.Text = ""; ApplicationCommands.Paste.Execute(null, editor);
                Assert.Equal("123.45", editor.Text);
                editor.IsReadOnly = true; editor.ApplyTemplate(); Assert.True(editor.IsReadOnly);
                combo.IsEnabled = false; Assert.False(combo.IsEnabled);
            }
            finally
            {
                if (previousClipboard is not null) Clipboard.SetDataObject(previousClipboard, true);
                window.Close();
            }
        });
    }

    [Fact]
    public void ContextMenuSubmenuTabsAndScrollbarsUseSharedTemplates()
    {
        RunSta(() =>
        {
            var panel = new StackPanel();
            var tab = new TabControl { Height = 130 };
            tab.Items.Add(new TabItem { Header = "参数", Content = new TextBox { Text = "测试参数" } });
            tab.Items.Add(new TabItem { Header = "结果", Content = new CheckBox { Content = "启用筛选", IsChecked = true } });
            panel.Children.Add(tab);
            var scrollbar = new ScrollBar { Orientation = Orientation.Horizontal, Maximum = 100, Value = 25, Height = 14, ViewportSize = 10 };
            panel.Children.Add(scrollbar);
            var button = new Button { Content = "操作菜单" }; panel.Children.Add(button);
            var menu = new ContextMenu();
            var parent = new MenuItem { Header = "添加算子" }; parent.Items.Add(new MenuItem { Header = "高斯滤波" }); menu.Items.Add(parent);
            menu.Items.Add(new MenuItem { Header = "保存", InputGestureText = "Ctrl+S" }); button.ContextMenu = menu;
            var window = CreateWindow(panel);
            try
            {
                window.Show(); window.UpdateLayout();
                tab.SelectedIndex = 1; tab.UpdateLayout();
                Assert.IsType<ContentPresenter>(tab.Template.FindName("PART_SelectedContentHost", tab));
                Assert.IsType<Track>(scrollbar.Template.FindName("PART_Track", scrollbar));
                menu.PlacementTarget = button; menu.IsOpen = true; Pump();
                Assert.NotNull(menu.Template);
                parent.IsSubmenuOpen = true; Pump();
                Assert.True(Assert.IsType<Popup>(parent.Template.FindName("PART_Popup", parent)).IsOpen);
                Assert.Equal(ColorConverter.ConvertFromString("#FFFFFF"), ((SolidColorBrush)menu.Background).Color);
                menu.IsOpen = false;
                Assert.False(MainWindow.IsTextEditing(button));
            }
            finally { menu.IsOpen = false; window.Close(); }
        });
    }

    [Fact]
    public async Task EditorLayoutRoundTripsWithoutChangingFormalProjects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionStudio-phase5-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new EditorWorkspaceLayoutStore(directory);
            Assert.Equal(new EditorWorkspaceLayout(), await store.LoadAsync());
            var expected = new EditorWorkspaceLayout(1, 400, 1.8, 1, "Image");
            await store.SaveAsync(expected);
            Assert.Equal(expected, await store.LoadAsync());
            await File.WriteAllTextAsync(store.FilePath, "{\"SchemaVersion\":9}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1093, 614)]
    [InlineData(911, 512)]
    public void CompactWorkspaceKeepsGraphImageAndActionsReachable(double width, double height)
    {
        RunStaAsync(async () =>
        {
            var state = Path.Combine(Path.GetTempPath(), "VisionStudio-phase5-window-" + Guid.NewGuid().ToString("N"));
            using var session = new EditorSession(); session.AddNode("ImageInput");
            var window = new MainWindow(session, state) { Width = width, Height = height, ShowActivated = false, Left = -10000, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
            try
            {
                window.Show(); await window.WorkspaceReady; window.Navigate(WorkspacePage.Editor);
                window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var graph = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("GraphView"));
                var image = Assert.IsType<ImageViewportControl>(window.FindName("Viewport"));
                var tabs = Assert.IsType<TabControl>(window.FindName("InspectorTabs"));
                Assert.True(graph.ActualHeight > 20, $"Graph height {graph.ActualHeight} at {width}x{height}");
                Assert.True(image.ViewportHeight > 10, $"Image canvas height {image.ViewportHeight} at {width}x{height}");
                Assert.True(tabs.ActualHeight > 80);
                foreach (var name in new[] { "MaximizeFlowButton", "MaximizeImageButton" })
                {
                    var button = Assert.IsType<Button>(window.FindName(name));
                    var origin = button.TranslatePoint(new Point(), window);
                    Assert.InRange(origin.Y, 0, window.ActualHeight - button.ActualHeight);
                    Assert.InRange(origin.X, 0, window.ActualWidth - button.ActualWidth);
                }
                var root = new DirectoryInfo(AppContext.BaseDirectory);
                while (root is not null && !File.Exists(Path.Combine(root.FullName, "VisionPlatform.sln"))) root = root.Parent;
                if (root is not null)
                {
                    var path = Path.Combine(root.FullName, "test-results", "phase5", "theme", $"workspace-{width}x{height}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(path); encoder.Save(stream);
                }
            }
            finally { await window.CloseEditorAsync(true); if (Directory.Exists(state)) Directory.Delete(state, true); }
        });
    }
    private static void RunStaAsync(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            { try { await action(); } catch (Exception ex) { error = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(40)), "Workspace layout timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static Window CreateWindow(UIElement content)
    {
        var window = new Window { Width = 500, Height = 320, Left = -10000, Top = 0, ShowActivated = false, Content = content };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") });
        return window;
    }
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Theme interaction timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}

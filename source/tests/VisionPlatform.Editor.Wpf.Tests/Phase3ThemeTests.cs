using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Graph;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase3ThemeTests
{
    [Fact]
    public void ToolboxSearchesChineseAndCaseInsensitiveIdsAndCombinesCategories()
    {
        var session = DispatchProxy.Create<IEditorSession, GraphEditorTests.SessionProbe>();
        using var state = new GraphEditorState(); state.Attach(session);
        state.SearchText = " 高斯 ";
        Assert.Equal("GaussianBlur", Assert.Single(state.Toolbox).TypeId);
        state.SearchText = "gaUSSIAN";
        Assert.Equal("GaussianBlur", Assert.Single(state.Toolbox).TypeId);
        state.SelectedCategory = "没有实现的分类"; Assert.Empty(state.Toolbox);
        state.SearchText = ""; state.SelectedCategory = "全部算法";
        Assert.Contains(state.Toolbox, item => item.TypeId == "ImageInput");
        session.Catalog.Register<TestParameters>("CustomProcessing", "定制处理", "自定义分类");
        Assert.Contains("自定义分类", state.Categories);
        state.SelectedCategory = "自定义分类";
        Assert.Equal("CustomProcessing", Assert.Single(state.Toolbox).TypeId);
        state.Attach(null); Assert.Empty(state.Toolbox);
        Assert.Equal("全部算法", Assert.Single(state.Categories));
    }

    [Fact]
    public void LightPaletteLoadsAndToolboxCardsKeepTheirSizeForLongNames()
    {
        RunSta(() =>
        {
            var session = DispatchProxy.Create<IEditorSession, GraphEditorTests.SessionProbe>();
            session.Catalog.Register<TestParameters>("LongCustomAlgorithmIdentifierForIndustrialProcessing", "非常长的工业视觉算法名称应该省略而不是改变卡片高度", "自定义分类");
            using var view = new GraphEditorView { Session = session, Width = 1000, Height = 1100 };
            var list = (ListBox)view.FindName("ToolboxList");
            VirtualizingPanel.SetIsVirtualizing(list, false);
            view.Measure(new Size(1000, 1100)); view.Arrange(new Rect(0, 0, 1000, 1100)); view.UpdateLayout();
            var sizes = new List<Size>();
            for (var i = 0; i < list.Items.Count; i++)
            {
                var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(i));
                container.ApplyTemplate();
                var card = Assert.IsType<Border>(VisualTreeHelper.GetChild(container, 0));
                sizes.Add(card.RenderSize); Assert.Equal(72, card.ActualHeight);
            }
            Assert.True(sizes.Count > 1);
            Assert.All(sizes, size => Assert.Equal(sizes[0].Width, size.Width));
            var palette = new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") };
            Assert.Equal(ColorConverter.ConvertFromString("#F5F9FF"), ((SolidColorBrush)palette["StudioBackground"]).Color);
            Assert.Equal(ColorConverter.ConvertFromString("#243B53"), ((SolidColorBrush)palette["StudioForeground"]).Color);
            var dark = new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/Dark.xaml") };
            foreach (var key in palette.Keys.Cast<object>()) Assert.True(dark.Contains(key), $"Alternate palette is missing {key}.");
        });
    }

    public sealed record TestParameters;
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF layout timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.Windows;
using System.Windows.Controls;

namespace VisionPlatform.Editor.Wpf.Collections;
/// <summary>Bounded page rendering; the original execution text remains unchanged.</summary>
public sealed class TextDetailsWindow : Window
{
    public TextDetailsWindow(string text)
    {
        Title = "当前执行 · 完整文本"; Width = 700; Height = 480; MinWidth = 350; MinHeight = 240; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "StudioBackground");
        var root = new DockPanel { Margin = new(14) }; Content = root;
        var actions = new WrapPanel(); DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var previous = new Button { Content = "上一页" }; var next = new Button { Content = "下一页" }; var copy = new Button { Content = "复制完整文本" };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 8) };
        actions.Children.Add(previous); actions.Children.Add(next); actions.Children.Add(copy); actions.Children.Add(status);
        var body = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(body); var page = 0; const int pageSize = 16384; var pages = Math.Max(1, (int)Math.Ceiling(text.Length / (double)pageSize));
        void Refresh() { body.Text = text.Length == 0 ? "空文本（本次执行没有文本内容）" : text.Substring(page * pageSize, Math.Min(pageSize, text.Length - page * pageSize)); status.Text = $"{page + 1}/{pages}页 · {text.Length}字符"; previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < pages; body.ScrollToHome(); }
        previous.Click += (_, _) => { page--; Refresh(); }; next.Click += (_, _) => { page++; Refresh(); }; copy.Click += (_, _) => Clipboard.SetText(text); Refresh();
    }
}

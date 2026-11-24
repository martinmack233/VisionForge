using System.Globalization;
using System.Windows;
using System.Windows.Data;
using VisionPlatform.Editor.Core.Workspace;

namespace VisionPlatform.Editor.Wpf.Workspace;
public sealed class WorkspacePageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = parameter?.ToString() ?? ""; var invert = text.StartsWith('!');
        var match = value is WorkspacePage page && page.ToString() == (invert ? text[1..] : text);
        return match != invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

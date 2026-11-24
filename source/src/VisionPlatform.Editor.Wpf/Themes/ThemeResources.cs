using System.Windows;
using System.Windows.Media;

namespace VisionPlatform.Editor.Wpf.Themes;

/// <summary>DrawingContext cannot use DynamicResource expressions. Resolve the same palette keys at paint time.</summary>
internal static class ThemeResources
{
    [ThreadStatic] private static ResourceDictionary? _fallback;
    public static Brush Brush(FrameworkElement owner, string key)
        => owner.TryFindResource(key) as Brush ?? (Brush)Fallback[key];

    private static ResourceDictionary Fallback => _fallback ??= new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml")
    };
}

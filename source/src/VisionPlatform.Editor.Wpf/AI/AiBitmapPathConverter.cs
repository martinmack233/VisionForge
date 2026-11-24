using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace VisionPlatform.Editor.Wpf.AI;

/// <summary>Thumbnails are small, owned decoded bitmaps. A gallery never retains a file handle on business images.</summary>
public sealed class AiBitmapPathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320; bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (IOException) { return null; }
        catch (NotSupportedException) { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

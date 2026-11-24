using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisionPlatform.Editor.Wpf.Imaging;

namespace VisionPlatform.Editor.Wpf.Devices;

public partial class DevicesPage : UserControl
{
    private DevicesViewModel? _bound;
    private bool _closed;
    public DevicesPage()
    {
        InitializeComponent(); DataContextChanged += OnDataContextChanged;
    }
    public ImageViewportControl PreviewViewport => DevicePreview;
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_bound is not null) _bound.WaitForPreviewAsync = null;
        _bound = e.NewValue as DevicesViewModel;
        if (_bound is not null) _bound.WaitForPreviewAsync = () => DevicePreview.WaitForDisplayAsync();
    }
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel { CanEditConnection: true } model) return;
        var dialog = new OpenFolderDialog { Title = "选择虚拟相机图像文件夹", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) model.FolderPath = dialog.FolderName;
    }
    public async Task CloseAsync()
    {
        if (_closed) return; _closed = true;
        DataContextChanged -= OnDataContextChanged;
        if (_bound is not null) _bound.WaitForPreviewAsync = null;
        DevicePreview.Dispose(); await DevicePreview.DrainAsync(); _bound = null;
    }
}

using System.Windows;
using System.Windows.Controls;
namespace VisionPlatform.Editor.Wpf.Workspace;
public partial class WorkspacePageHost : UserControl
{
    public WorkspacePageHost() => InitializeComponent();
    public event EventHandler? SetupRequested;
    public event EventHandler? PackagesRequested;
    private void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke(this, EventArgs.Empty);
    private void Packages_Click(object sender, RoutedEventArgs e) => PackagesRequested?.Invoke(this, EventArgs.Empty);
    public void SetDevicesPage(FrameworkElement page) => DevicesContent.Content = page;
    public void SetExamplesPage(FrameworkElement page) => ExamplesContent.Content = page;
    public void SetAiPage(FrameworkElement page) => AiContent.Content = page;
}

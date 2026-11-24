using System.Windows.Controls;
using System.Windows;

namespace VisionPlatform.Editor.Wpf.Examples;
public partial class ExamplesPage:UserControl
{
    public ExamplesPage()=>InitializeComponent();
    public event EventHandler? AiStarterRequested;
    private void AiStarter_Click(object sender, RoutedEventArgs e) => AiStarterRequested?.Invoke(this, EventArgs.Empty);
}

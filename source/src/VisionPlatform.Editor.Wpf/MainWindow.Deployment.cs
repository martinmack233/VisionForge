using System.Windows;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Editor.Wpf.Deployment;

namespace VisionPlatform.Editor.Wpf;
public partial class MainWindow
{
    private SetupWindow? _setupWindow;
    public void ShowSetup()
    {
        if (_setupWindow is { IsVisible: true }) { _setupWindow.Activate(); return; }
        _setupWindow = new SetupWindow { Owner = this };
        _setupWindow.Saved += async configuration =>
        {
            try
            {
                Workspace.DefaultProjectDirectory = System.IO.Path.Combine(configuration.DataDirectory, "Projects"); Workspace.LogDirectory = configuration.LogDirectory;
                await Workspace.SaveSettingsAsync();
                if (!AiPage.ViewModel.IsConnected) AiPage.ViewModel.ApplyTrainingMachineConfiguration(configuration);
                Session.SetStatus("本机配置已应用；没有启动服务或设备。相机适配器路径在重新启动程序后生效，已连接的任务不受改动。");
            }
            catch (Exception ex) { Session.SetStatus("机器配置已保存，当前工作区设置未完全同步：" + ex.Message); }
        };
        _setupWindow.Show();
    }
    public void ShowFirstSetupIfNeeded()
    {
        try { if (!new MachineConfigurationStore().Load().SetupCompleted) ShowSetup(); }
        catch (Exception ex) { Session.SetStatus("机器配置需要检查：" + ex.Message); ShowSetup(); }
    }
    public void ShowProjectPackages()
    {
        if (Session.IsBusy) { Session.SetStatus("请先停止检测，再进行工程包操作。"); return; }
        var window = new ProjectPackageWindow(Session, () => SaveProject(false)) { Owner = this, OpenProject = path => Workspace.OpenProjectAsync(path) }; window.Show();
    }
    private void Setup_Click(object sender, RoutedEventArgs e) => ShowSetup();
    private void Packages_Click(object sender, RoutedEventArgs e) => ShowProjectPackages();
}

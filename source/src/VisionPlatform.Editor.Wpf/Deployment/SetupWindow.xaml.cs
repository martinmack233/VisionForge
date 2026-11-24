using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Editor.Core.Deployment;

namespace VisionPlatform.Editor.Wpf.Deployment;
public partial class SetupWindow : Window
{
    private readonly MachineConfigurationStore _store;
    private CancellationTokenSource? _operation;
    private int _revision;
    public SetupReport? Report { get; private set; }
    public event Action<MachineConfiguration>? Saved;
    public SetupWindow(MachineConfigurationStore? store = null)
    {
        InitializeComponent(); _store = store ?? new();
        try { SetConfiguration(_store.Load()); } catch (Exception ex) { Notice.Text = ex.Message; SetConfiguration(new()); }
        ComponentDestination.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "Components", "yolo-detection-1.0.0");
        foreach (var box in new[] { DetectionPython, DetectionComponent, TrainingRoot, TrainingPython, BoqSdk, BoqWorker, DriverDirectory, DataDirectory, CacheDirectory, LogDirectory, ModelPath })
            box.TextChanged += (_, _) => InvalidateReport();
        TrainingEnabled.Checked += (_, _) => InvalidateReport(); TrainingEnabled.Unchecked += (_, _) => InvalidateReport();
        Closed += (_, _) => _operation?.Cancel();
    }
    private void InvalidateReport()
    {
        _revision++; Report = null; Results.ItemsSource = null; ExportButton.IsEnabled = false;
        if (_operation is null) Notice.Text = "配置已更改，原检查结果已失效，请重新检查。未执行检测、训练或连接设备。";
    }
    public void SetConfiguration(MachineConfiguration c)
    {
        DetectionPython.Text = c.DetectionPython; DetectionComponent.Text = c.DetectionComponentRoot; TrainingEnabled.IsChecked = c.TrainingEnabled;
        TrainingRoot.Text = c.TrainingRoot; TrainingPython.Text = c.TrainingPython; BoqSdk.Text = c.BoqSdkPath;
        BoqWorker.Text = c.BoqWorkerPath.Length > 0 ? c.BoqWorkerPath : Path.Combine(AppContext.BaseDirectory, "CameraAdapters", "Boq", "VisionPlatform.BoqWorker.exe");
        DriverDirectory.Text = c.VendorDriverDirectory; DataDirectory.Text = c.DataDirectory; CacheDirectory.Text = c.CacheDirectory; LogDirectory.Text = c.LogDirectory;
    }
    public MachineConfiguration Configuration => new() { DetectionPython = DetectionPython.Text.Trim(), DetectionComponentRoot = DetectionComponent.Text.Trim(), TrainingEnabled = TrainingEnabled.IsChecked == true,
        TrainingRoot = TrainingRoot.Text.Trim(), TrainingPython = TrainingPython.Text.Trim(), BoqSdkPath = BoqSdk.Text.Trim(), BoqWorkerPath = BoqWorker.Text.Trim(), VendorDriverDirectory = DriverDirectory.Text.Trim(),
        DataDirectory = DataDirectory.Text.Trim(), CacheDirectory = CacheDirectory.Text.Trim(), LogDirectory = LogDirectory.Text.Trim() };
    public async Task CheckAsync()
    {
        if (_operation is not null) return; using var operation = new CancellationTokenSource(); _operation = operation; CheckButton.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            var revision = _revision; var snapshot = Configuration; var model = ModelPath.Text.Trim();
            Notice.Text = "正在检查，不执行检测/训练，不连接相机…";
            var report = await SetupDiagnostics.CheckAsync(snapshot, model, operation.Token);
            if (revision != _revision) { Notice.Text = "检查过程中配置已更改，结果未采用，请重新检查。"; return; }
            Report = report; Results.ItemsSource = Report.Checks; ExportButton.IsEnabled = true;
            Notice.Text = "检查完成。可选组件未配置不阻止核心检测；未验证项不表示通过。";
        }
        catch (OperationCanceledException) { Notice.Text = "检查已取消；没有改变环境、模型或设备。"; }
        catch (Exception ex) { Notice.Text = ex.Message; }
        finally { _operation = null; CheckButton.IsEnabled = SaveButton.IsEnabled = true; }
    }
    public async Task SaveConfigurationAsync()
    {
        var c = Configuration with { SetupCompleted = true }; await _store.SaveAsync(c); Saved?.Invoke(c); Notice.Text = "本机配置已保存；不会执行算法或连接服务。检测/相机配置在后续运行或重新打开设备页时读取；原配置保留.previous备份。";
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) { try { await SaveConfigurationAsync(); } catch (Exception ex) { Notice.Text = ex.Message; } }
    private async void Export_Click(object sender, RoutedEventArgs e)
    { if (Report is null) return; var d = new SaveFileDialog { Filter = "脱敏诊断 (*.json)|*.json", FileName = "VisionForge-diagnostics.json" }; if (d.ShowDialog(this) == true) try { await SetupDiagnostics.ExportRedactedAsync(d.FileName, Report); Notice.Text = "已导出脱敏诊断，不含机器路径或业务数据。"; } catch (Exception ex) { Notice.Text = ex.Message; } }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || LocalRights.IsChecked != true) return; using var operation = new CancellationTokenSource(); _operation = operation; InstallButton.IsEnabled = CheckButton.IsEnabled = SaveButton.IsEnabled = false;
        try { await DetectionComponentTool.RunAsync(DetectionPython.Text.Trim(), ["install", "--source", ComponentSource.Text.Trim(), "--destination", ComponentDestination.Text.Trim(), "--recipe", DetectionComponentTool.RecipePath, "--ack-local-use"], operation.Token); DetectionComponent.Text = Path.GetFullPath(ComponentDestination.Text.Trim()); Notice.Text = "本机检测组件已制作。请重新检查并保存；没有安装依赖、打包环境或确认商业授权。"; }
        catch (OperationCanceledException) { Notice.Text = "制作已取消。"; } catch (Exception ex) { Notice.Text = ex.Message; }
        finally { _operation = null; InstallButton.IsEnabled = LocalRights.IsChecked == true; CheckButton.IsEnabled = SaveButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void File(System.Windows.Controls.TextBox box, string filter) { var d = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (d.ShowDialog(this) == true) box.Text = Path.GetFullPath(d.FileName); }
    private void Folder(System.Windows.Controls.TextBox box) { var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) box.Text = Path.GetFullPath(d.FolderName); }
    private void ChooseDetectionPython_Click(object s, RoutedEventArgs e) => File(DetectionPython, "Python (*.exe)|*.exe");
    private void ChooseComponent_Click(object s, RoutedEventArgs e) => Folder(DetectionComponent);
    private void ChooseModel_Click(object s, RoutedEventArgs e) => File(ModelPath, "ONNX (*.onnx)|*.onnx");
    private void ChooseSource_Click(object s, RoutedEventArgs e) => Folder(ComponentSource);
    private void ChooseTrainingRoot_Click(object s, RoutedEventArgs e) => Folder(TrainingRoot);
    private void ChooseTrainingPython_Click(object s, RoutedEventArgs e) => File(TrainingPython, "Python (*.exe)|*.exe");
    private void ChooseSdk_Click(object s, RoutedEventArgs e) => File(BoqSdk, "SDK (*.dll)|*.dll");
    private void ChooseWorker_Click(object s, RoutedEventArgs e) => File(BoqWorker, "Worker (*.exe)|*.exe");
    private void ChooseDriver_Click(object s, RoutedEventArgs e) => Folder(DriverDirectory);
}

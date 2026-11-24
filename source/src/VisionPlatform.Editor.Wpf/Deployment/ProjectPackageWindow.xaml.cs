using System.IO;
using System.Windows;
using Microsoft.Win32;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Wpf.Deployment;
public sealed class PackageResourceRow(PackageResource resource)
{
    public PackageResource Resource { get; } = resource;
    public string Id => Resource.Id;
    public string Kind => Resource.Kind;
    public string Name => Path.GetFileName(Resource.PortablePath);
    public string Mode => Resource.Included ? "随包携带" : "需外部定位";
    public string RelocationPath { get; set; } = "";
}
public partial class ProjectPackageWindow : Window
{
    private readonly IEditorSession _session;
    private readonly Func<Task<bool>> _save;
    private readonly ProjectPackageStore _packages;
    private CancellationTokenSource? _operation;
    public PackageInspection? Inspection { get; private set; }
    public IReadOnlyList<PackageResourceRow> ResourceRows { get; private set; } = [];
    public string? ImportedProject { get; private set; }
    public Func<string, Task>? OpenProject { get; set; }
    public ProjectPackageWindow(IEditorSession session, Func<Task<bool>> save)
    {
        _session = session; _save = save; _packages = new(session.Registry, new Dictionary<string, string> { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" });
        InitializeComponent(); SourceProject.Text = session.ProjectPath ?? "尚未保存"; Closed += (_, _) => _operation?.Cancel();
    }
    public async Task InspectPackageAsync(string path)
    {
        Inspection = await Task.Run(() => _packages.InspectAsync(path, _operation?.Token ?? default)); ResourceRows = Inspection.Manifest.Resources.Select(r => new PackageResourceRow(r)).ToArray(); ResourceGrid.ItemsSource = ResourceRows;
        ImportButton.IsEnabled = Inspection.CanImport; ImportName.Text = Inspection.Manifest.ProjectName;
        InspectionSummary.Text = Inspection.CanImport ? $"包格式{Inspection.Manifest.PackageVersion} / 工程Schema{Inspection.Manifest.ProjectSchema}；{ResourceRows.Count}项资源，{Inspection.ExternalResources.Count}项需显式定位。依赖与内容哈希通过。" : "预检拒绝：" + string.Join("\n", Inspection.Problems);
        if (Inspection.Manifest.AiProjectId is not null) InspectionSummary.Text += "\nAI项目关联：" + Inspection.Manifest.AiProjectId + "；未携带业务数据目录，导入后需显式重新关联。";
    }
    public async Task ImportPackageAsync(string package, string destination, string? name = null)
    {
        var relocations = ResourceRows.Where(r => !r.Resource.Included).ToDictionary(r => r.Id, r => r.RelocationPath.Trim());
        ImportedProject = await Task.Run(() => _packages.ImportAsync(package, new(destination, name, relocations), _operation?.Token ?? default)); OpenImportedButton.IsEnabled = true;
        Status.Text = "导入成功：" + ImportedProject + "。未执行算法、连接设备或发送通信。";
    }
    private async Task Work(Func<Task> action)
    {
        if (_operation is not null) return; using var operation = new CancellationTokenSource(); _operation = operation;
        ExportButton.IsEnabled = ImportButton.IsEnabled = false;
        try { await action(); } catch (OperationCanceledException) { Status.Text = "已取消；没有提交半成品工程。"; } catch (Exception ex) { Status.Text = ex.Message; }
        finally { _operation = null; ExportButton.IsEnabled = true; ImportButton.IsEnabled = Inspection?.CanImport == true; }
    }
    private async void SaveProject_Click(object sender, RoutedEventArgs e) { if (await _save()) SourceProject.Text = _session.ProjectPath; }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsBusy || _session.HasChanges || _session.ProjectPath is null) { Status.Text = "请先停止运行并保存当前工程，再导出；不会使用旧磁盘参数代替未保存修改。"; return; }
        var dialog = new SaveFileDialog { Filter = "视构 VisionForge工程包 (*.vspkg)|*.vspkg", FileName = "Inspection.vspkg", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var exportOptions = new PackageExportOptions(IncludeModels.IsChecked == true, Rights.IsChecked == true); var sourcePath = _session.ProjectPath;
        await Work(async () => { var manifest = await Task.Run(() => _packages.ExportAsync(sourcePath, dialog.FileName, exportOptions, _operation!.Token)); ExportSummary.Text = $"导出完成：包格式{manifest.PackageVersion} / {manifest.Resources.Count}项资源 / {manifest.Files.Count}个文件。\n" + string.Join("\n", manifest.Resources.Select(r => r.Id + " · " + r.Kind + (r.Included ? " · 随包" : " · 外部引用"))); Status.Text = "已导出新包，原工程及资源保持原样。"; });
    }
    private void ChoosePackage_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "视构 VisionForge工程包 (*.vspkg)|*.vspkg", InitialDirectory = Path.Combine(AppContext.BaseDirectory, "ProjectPackages") }; if (d.ShowDialog(this) == true) PackagePath.Text = Path.GetFullPath(d.FileName); }
    private void ChooseDestination_Click(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) Destination.Text = Path.Combine(d.FolderName, "Imported-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); }
    private void PackagePath_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (ImportButton is null) return; Inspection = null; ImportButton.IsEnabled = false; ResourceRows = []; ResourceGrid.ItemsSource = null; }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await Work(() => InspectPackageAsync(PackagePath.Text.Trim()));
    private async void Import_Click(object sender, RoutedEventArgs e) => await Work(() => ImportPackageAsync(PackagePath.Text.Trim(), Destination.Text.Trim(), ImportName.Text));
    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (ResourceGrid.SelectedItem is not PackageResourceRow row || row.Resource.Included) { Status.Text = "请选择需外部定位的资源。"; return; }
        if (row.Kind is "VirtualFolder" or "OcrLanguage") { var d = new OpenFolderDialog(); if (d.ShowDialog(this) == true) row.RelocationPath = Path.GetFullPath(d.FolderName); }
        else { var d = new OpenFileDialog { Filter = row.Kind == "Model" ? "ONNX (*.onnx)|*.onnx" : "资源文件|*.*", CheckFileExists = true }; if (d.ShowDialog(this) == true) row.RelocationPath = Path.GetFullPath(d.FileName); }
        ResourceGrid.Items.Refresh();
    }
    private async void OpenImported_Click(object sender, RoutedEventArgs e) { if (ImportedProject is not null && OpenProject is not null) try { await OpenProject(ImportedProject); Close(); } catch (Exception ex) { Status.Text = ex.Message; } }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

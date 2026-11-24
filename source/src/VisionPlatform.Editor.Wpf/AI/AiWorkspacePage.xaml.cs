using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.AI;

public partial class AiWorkspacePage : UserControl, IAsyncDisposable
{
    public AiWorkspaceViewModel ViewModel { get; }
    public bool HasRunningTasks => ViewModel.HasRunningTasks;
    public AiWorkspacePage(IEditorSession session, IAiWorkspaceClient client, AiServiceSettings settings)
    {
        ViewModel = new(session, client, settings); InitializeComponent(); DataContext = ViewModel;
        // Keep a usable image/annotation surface at small windows and large text scales.
        // The outer page scrolls when necessary instead of compressing the image area to zero.
        SizeChanged += (_, args) => AiPageBody.Height = Math.Max(720, args.NewSize.Height - 32);
        AnnotationCanvas.EditStarted += ViewModel.BeginAnnotationEdit;
        AnnotationCanvas.Edited += ViewModel.AnnotationEdited;
        AnnotationCanvas.UndoRequested += ViewModel.UndoAnnotation;
        AnnotationCanvas.DeleteRequested += ViewModel.DeleteAnnotation;
        AnnotationCanvas.ClassRequested += id => ViewModel.SelectedClass = ViewModel.Classes.FirstOrDefault(c => c.Id == id);
        AnnotationCanvas.ImageNavigationRequested += delta => Run(() => ViewModel.NavigateImageAsync(delta));
        AnnotationCanvas.CoordinateChanged += text => AnnotationCoordinates.Text = text;
        AnnotationCanvas.PreviewError += text => _ = ViewModel.RunUiAsync(() => throw new InvalidOperationException(text));
        AnnotationCanvas.NativeRegionRequested += region => _ = ViewModel.RequestNativeTileAsync(region.X, region.Y, region.Width, region.Height);
        ViewModel.PresentNativeTileAsync = AnnotationCanvas.SetNativeTileAsync;
        ViewModel.TrainingValidationFailed += field => Dispatcher.BeginInvoke(new Action(() =>
        {
            AiTabs.SelectedIndex = 3;
            FrameworkElement target = field switch { "Model" => TrainingModelInput, "Dataset" => TrainingDatasetInput, "Settings" => TrainingEpochsInput, _ => ConnectWorkspaceButton };
            target.BringIntoView(); target.Focus(); if (target is TextBox box) box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        ViewModel.TestPageRequested += () => AiTabs.SelectedIndex = 4;
        ViewModel.TrainingPageRequested += () => { AiTabs.SelectedIndex = 3; Dispatcher.BeginInvoke(new Action(() => TrainingDatasetInput.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded); };
    }
    public Task InitializeAsync() => ViewModel.InitializeAsync();
    public Task<bool> PrepareCloseAsync(Func<string, Task<AiCloseChoice>> confirm) => ViewModel.PrepareCloseAsync(confirm);
    public ValueTask DisposeAsync() => ViewModel.DisposeAsync();
    private async void Run(Func<Task> action) => await ViewModel.RunUiAsync(action);
    private Window? Owner => Window.GetWindow(this);
    private void Connect_Click(object sender, RoutedEventArgs e) => Run(ViewModel.ConnectAsync);
    private void Diagnose_Click(object sender, RoutedEventArgs e) => Run(ViewModel.DiagnoseAsync);
    private void Refresh_Click(object sender, RoutedEventArgs e) => Run(async () => { await ViewModel.RefreshProjectsAsync(); if (ViewModel.HasProject) { await ViewModel.RefreshImagesAsync(); await ViewModel.RefreshModelsAsync(); await ViewModel.RefreshHistoryAsync(); } });
    private void CreateProject_Click(object sender, RoutedEventArgs e) => Run(ViewModel.CreateProjectAsync);
    private void RenameProject_Click(object sender, RoutedEventArgs e) => Run(ViewModel.RenameProjectAsync);
    private void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is not { } project) return;
        if (MessageBox.Show(Owner, $"永久删除 AI 项目“{project.Name}”？\n将删除此AI项目独立目录内的图片副本、标注、训练和检测输出。检测工程不会因此自动删除。\n项目ID：{project.Id}", "删除AI项目", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(Owner, "请再次确认已备份需要的AI数据。此操作不可撤销。", "再次确认AI项目身份", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Run(ViewModel.DeleteProjectAsync);
    }
    private void LinkProject_Click(object sender, RoutedEventArgs e) => Run(ViewModel.LinkProjectToInspectionAsync);
    private void ChooseYoloRoot_Click(object sender, RoutedEventArgs e) => ChooseFolder(path => { ViewModel.YoloRoot = path; ViewModel.PythonPath = Path.Combine(path, ".venv", "Scripts", "python.exe"); });
    private void ChoosePython_Click(object sender, RoutedEventArgs e) => ChooseFile("Python 解释器 (*.exe)|*.exe", path => ViewModel.PythonPath = path);
    private void ChooseDataHome_Click(object sender, RoutedEventArgs e) => ChooseFolder(path => ViewModel.DataHome = path);
    private void ChooseFolder(Action<string> accept)
    { var dialog = new OpenFolderDialog { Title = "选择文件夹", Multiselect = false }; if (dialog.ShowDialog(Owner) == true) { accept(dialog.FolderName); RefreshEditableBindings(); } }
    private void ChooseFile(string filter, Action<string> accept)
    { var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true }; if (dialog.ShowDialog(Owner) == true) { accept(Path.GetFullPath(dialog.FileName)); RefreshEditableBindings(); } }
    private void RefreshEditableBindings() { var context = DataContext; DataContext = null; DataContext = context; }
    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    { var dialog = ImageDialog(true); if (dialog.ShowDialog(Owner) == true) Run(() => ViewModel.ImportAsync(dialog.FileNames, false)); }
    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "选择导入图片文件夹" }; if (dialog.ShowDialog(Owner) == true) Run(() => ViewModel.ImportAsync([dialog.FolderName], RecursiveImport.IsChecked == true)); }
    private static OpenFileDialog ImageDialog(bool multi) => new() { Title = "选择原始图片", Multiselect = multi, Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*" };
    private AiImageItem[] GallerySelection() => ImageGallery.SelectedItems.Cast<AiImageItem>().ToArray();
    private void ImageSelection_Changed(object sender, SelectionChangedEventArgs e)
    { if (ImageGallery.SelectedItem is AiImageItem image) ViewModel.SelectedImage = image; }
    private void Filter_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshImagesAsync());
    private void PreviousPage_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshImagesAsync(-1));
    private void NextPage_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshImagesAsync(1));
    private void MarkReviewed_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.ReviewAsync(GallerySelection(), true));
    private void MarkUnreviewed_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.ReviewAsync(GallerySelection(), false));
    private void AssignBatch_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.AssignBatchAsync(GallerySelection()));
    private void DeleteImages_Click(object sender, RoutedEventArgs e)
    { var selected = GallerySelection(); if (selected.Length > 0 && MessageBox.Show(Owner, $"删除所选 {selected.Length} 张图片记录及关联标注？外部原图及历史输出按原YOLOStudio保护规则保留。", "删除图片记录", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Run(() => ViewModel.DeleteImagesAsync(selected)); }
    private void AnnotateSelected_Click(object sender, RoutedEventArgs e) { if (ImageGallery.SelectedItem is AiImageItem image) ViewModel.SelectedImage = image; AiTabs.SelectedIndex = 1; }
    private void ReviewCurrent_Click(object sender, RoutedEventArgs e) => Run(async () => { await ViewModel.FlushAnnotationsAsync(); if (ViewModel.SelectedImage is { } image) await ViewModel.ReviewAsync([image], true); });
    private void AddClass_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.SaveClassAsync(true));
    private void SaveClass_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.SaveClassAsync(false));
    private void DrawAnnotation_Click(object sender, RoutedEventArgs e) { AnnotationCanvas.DrawMode = true; AnnotationCanvas.Focus(); }
    private void SelectAnnotation_Click(object sender, RoutedEventArgs e) { AnnotationCanvas.DrawMode = false; AnnotationCanvas.Focus(); }
    private void AnnotationFit_Click(object sender, RoutedEventArgs e) => AnnotationCanvas.Fit();
    private void AnnotationOneToOne_Click(object sender, RoutedEventArgs e) => AnnotationCanvas.OneToOne();
    private void UndoAnnotation_Click(object sender, RoutedEventArgs e) => ViewModel.UndoAnnotation();
    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteAnnotation();
    private void ApplyCoordinates_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyAnnotationCoordinates();
    private void SaveAnnotations_Click(object sender, RoutedEventArgs e) => Run(ViewModel.FlushAnnotationsAsync);
    public void OpenStarterGuide() { AiTabs.SelectedIndex = 0; StarterGuide.IsExpanded = true; StarterGuide.BringIntoView(); }
    private void PrepareStarter_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.PrepareStarterExampleAsync(false));
    private void NewStarter_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.PrepareStarterExampleAsync(true));
    private void OpenStarterResources_Click(object sender, RoutedEventArgs e) => OpenLocation(ViewModel.StarterResourceDirectory);
    private void UseDatasetForTraining_Click(object sender, RoutedEventArgs e) => Run(() => { ViewModel.UsePreparedDatasetForTraining(); return Task.CompletedTask; });
    private void PreviewSlices_Click(object sender, RoutedEventArgs e) => Run(ViewModel.PreviewSlicesAsync);
    private void StartSlices_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartSlicesAsync(false));
    private void BuildDataset_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartSlicesAsync(true));
    private void SavePreprocessing_Click(object sender, RoutedEventArgs e) => Run(ViewModel.SavePreprocessingAsync);
    private void ImportLabels_Click(object sender, RoutedEventArgs e) => ChooseFolder(path => Run(() => ViewModel.TransferLabelsAsync(path, true)));
    private void ExportLabels_Click(object sender, RoutedEventArgs e) => ChooseFolder(path => Run(() => ViewModel.TransferLabelsAsync(path, false)));
    private void Analyze_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.AnalyzeAsync());
    private void ChooseTrainingDataset_Click(object sender, RoutedEventArgs e) => ChooseFile("YOLO 数据集 (*.yaml;*.yml)|*.yaml;*.yml", path => ViewModel.TrainingDataset = path);
    private void ChooseTrainingModel_Click(object sender, RoutedEventArgs e) => ChooseFile("PyTorch 权重 (*.pt)|*.pt", path => ViewModel.TrainingModel = path);
    private void Train_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartTrainingAsync(false));
    private void StopTraining_Click(object sender, RoutedEventArgs e) => Run(ViewModel.StopTrainingAsync);
    private void ResumeTraining_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartTrainingAsync(true));
    private void ChooseTestModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择已有检测模型", Filter = "检测模型 (*.pt;*.onnx)|*.pt;*.onnx", CheckFileExists = true,
            InitialDirectory = ViewModel.ModelBrowseDirectory, ClientGuid = new Guid("caef8179-d4c1-435d-985f-07d69ab56ce1") };
        if (dialog.ShowDialog(Owner) == true) Run(async () => { ViewModel.TestModelPath = Path.GetFullPath(dialog.FileName); ViewModel.TestBackend = Path.GetExtension(dialog.FileName).Equals(".onnx", StringComparison.OrdinalIgnoreCase) ? "onnx" : "pt"; await ViewModel.InspectTestModelAsync(); });
    }
    private void UseModelForTest_Click(object sender, RoutedEventArgs e) => Run(ViewModel.UseSelectedModelForTestAsync);
    private void InspectTestInput_Click(object sender, RoutedEventArgs e) => Run(ViewModel.InspectTestModelAsync);
    private void ChooseTestImages_Click(object sender, RoutedEventArgs e)
    { var dialog = ImageDialog(true); if (dialog.ShowDialog(Owner) == true) { ViewModel.TestInputsText = JoinInputs(ViewModel.TestInputsText, dialog.FileNames); RefreshEditableBindings(); } }
    private void ChooseTestFolder_Click(object sender, RoutedEventArgs e) => ChooseFolder(path => ViewModel.TestInputsText = JoinInputs(ViewModel.TestInputsText, [path]));
    private static string JoinInputs(string current, IEnumerable<string> paths) => string.Join(Environment.NewLine, current.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase));
    private void UseProjectImages_Click(object sender, RoutedEventArgs e) { ViewModel.TestInputsText = string.Join(Environment.NewLine, ViewModel.Images.Select(i => i.Path)); RefreshEditableBindings(); }
    private void Infer_Click(object sender, RoutedEventArgs e) => Run(ViewModel.StartInferenceAsync);
    private void RefreshResults_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshInferenceResultsAsync());
    private void PreviousResultPage_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshInferenceResultsAsync(pageDelta: -1));
    private void NextResultPage_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.RefreshInferenceResultsAsync(pageDelta: 1));
    private void Evaluate_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartEvaluationAsync(false));
    private void Compare_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.StartEvaluationAsync(true));
    private void RefreshModels_Click(object sender, RoutedEventArgs e) => Run(ViewModel.RefreshModelsAsync);
    private void ExportModel_Click(object sender, RoutedEventArgs e) => Run(ViewModel.ExportModelAsync);
    private void InspectDeployment_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.InspectDeploymentAsync());
    private void ApplyModel_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.ApplyModelToNodeAsync(false, AiTabs.SelectedIndex == 4));
    private void CreateYoloNode_Click(object sender, RoutedEventArgs e) => Run(() => ViewModel.ApplyModelToNodeAsync(true, AiTabs.SelectedIndex == 4));
    private void CancelTask_Click(object sender, RoutedEventArgs e) => Run(ViewModel.CancelTaskAsync);
    private void OpenImageLocation_Click(object sender, RoutedEventArgs e) => OpenLocation(ViewModel.SelectedImage?.Path);
    private void OpenModelLocation_Click(object sender, RoutedEventArgs e) => OpenLocation(ViewModel.SelectedModel?.Path);
    private void OpenTrainingOutput_Click(object sender, RoutedEventArgs e) => OpenLocation(ViewModel.OutputFolder);
    private void OpenLocation(string? path) => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("尚无可打开的输出位置。");
        var location = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (location is null || !Directory.Exists(location)) throw new DirectoryNotFoundException("位置不存在：" + path);
        Process.Start(new ProcessStartInfo(location) { UseShellExecute = true }); return Task.CompletedTask;
    });
    private void DetectionSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel.SelectedDetection is not { } detection) return;
        TestCanvas.SelectedBox = ViewModel.DetectionBoxes.FirstOrDefault(b => b.ClassId == detection.ClassId && Math.Abs(b.X - detection.X) < .001 && Math.Abs(b.Y - detection.Y) < .001);
    }
}

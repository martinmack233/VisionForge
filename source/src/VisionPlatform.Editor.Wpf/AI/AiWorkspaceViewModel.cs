using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using VisionPlatform.AI;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.AI;

/// <summary>Presentation/orchestration only. All database, image preparation, training and inference business work is delegated to YOLOStudio services.</summary>
public sealed partial class AiWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAiWorkspaceClient _client;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _poll;
    private CancellationTokenSource? _saveCancellation;
    private Task _saveTask = Task.CompletedTask;
    private bool _busy, _connected, _disposed, _polling, _loadingAnnotations, _annotationDirty;
    private string _status = "配置已有 Python 环境后连接 AI 工作区；普通视觉功能可独立使用。", _error = "", _workspaceId = "", _annotationRevision = "";
    private AiProjectItem? _project;
    private AiImageItem? _image;
    private AiClassItem? _class;
    private AiBox? _box;
    private AiModelItem? _model;
    private AiTaskItem? _task;
    private AiDetectionItem? _detection;
    private AiInferenceImageItem? _inferenceImage;
    private int _resultPage, _resultTotal;
    private string _previewPath = "", _annotationState = "尚未选择图片", _log = "", _diagnostic = "尚未连接", _deploymentInfo = "", _testSummary = "";
    private int _imageWidth, _imageHeight, _page, _total;
    private long _imageRequest, _logOffset;
    private string? _trainingTaskId, _inferenceTaskId;
    private JsonElement _verifiedDeployment;
    private readonly Stack<AiBox[]> _annotationUndo = new();
    private readonly SemaphoreSlim _annotationSaveGate = new(1, 1);
    private AiImageItem? _loadedImage;
    private string? _loadedProjectId;
    private long _annotationEditSerial;
    private readonly SemaphoreSlim _tileGate = new(1, 1);
    private long _tileVersion;
    private readonly HashSet<string> _completed = [];

    public AiWorkspaceViewModel(IEditorSession session, IAiWorkspaceClient client, AiServiceSettings settings)
    {
        Session = session; _client = client; _dispatcher = Dispatcher.CurrentDispatcher;
        YoloRoot = settings.YoloRoot; PythonPath = settings.PythonPath; DataHome = settings.DataHome; TransferRoot = settings.TransferRoot; ServiceScript = settings.ServiceScript;
        _client.EventReceived += ServiceEvent;
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await PollAsync(), _dispatcher);
        _poll.Stop();
        InitializeAnnotationEditor();
    }
    public IEditorSession Session { get; }
    public Func<AiProjectSelection, Task>? LinkProjectAsync { get; set; }
    public Func<AiModelSelection, bool, Task>? ApplyModelAsync { get; set; }
    public Func<AiServiceSettings, Task>? SaveSettingsAsync { get; set; }
    public ObservableCollection<AiProjectItem> Projects { get; } = [];
    public ObservableCollection<AiImageItem> Images { get; } = [];
    public ObservableCollection<AiClassItem> Classes { get; } = [];
    public ObservableCollection<AiBox> Boxes { get; } = [];
    public ObservableCollection<AiModelItem> Models { get; } = [];
    public ObservableCollection<AiTaskItem> Tasks { get; } = [];
    public ObservableCollection<AiTaskItem> TrainingHistory { get; } = [];
    public ObservableCollection<AiMetricPoint> Metrics { get; } = [];
    public ObservableCollection<AiDetectionItem> Detections { get; } = [];
    public ObservableCollection<AiBox> DetectionBoxes { get; } = [];
    public ObservableCollection<AiClassItem> DetectionClasses { get; } = [];
    public ObservableCollection<AiBox> SliceBoxes { get; } = [];
    public ObservableCollection<AiInferenceImageItem> InferenceImages { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<string> DatasetPaths { get; } = [];
    public string YoloRoot { get; set; }
    public string PythonPath { get; set; }
    public string DataHome { get; set; }
    public string TransferRoot { get; set; }
    public string ServiceScript { get; set; }
    public string NewProjectName { get; set; } = "新的 AI 项目";
    public string SearchText { get; set; } = "";
    public string ReviewFilter { get; set; } = "全部";
    public IReadOnlyList<string> ReviewFilters { get; } = ["全部", "未检查", "已检查", "正常样本", "有标注"];
    public string BatchName { get; set; } = "";
    public string ClassName { get; set; } = "缺陷";
    public string ClassColor { get; set; } = "#D94A53";
    public string TileWidthText { get; set; } = "1024";
    public string TileHeightText { get; set; } = "1024";
    public string OverlapText { get; set; } = "0.15";
    public string BoundaryMode { get; set; } = "末端对齐 / 小图零填充";
    public IReadOnlyList<string> BoundaryModes { get; } = ["末端对齐 / 小图零填充"];
    public string TileIndexText { get; set; } = "-1";
    public string Grouping { get; set; } = "batch";
    public IReadOnlyList<string> Groupings { get; } = ["batch", "image"];
    public string TrainRatioText { get; set; } = "0.8";
    public string ValidationRatioText { get; set; } = "0.1";
    public string TestRatioText { get; set; } = "0.1";
    public bool DatasetTiled { get; set; } = true;
    public string DatasetOutput { get; set; } = "";
    public string GrayMapping { get; set; } = "percentile";
    public IReadOnlyList<string> GrayMappings { get; } = ["percentile", "range"];
    public string GrayLowText { get; set; } = "1";
    public string GrayHighText { get; set; } = "99";
    public string GrayFixedLowText { get; set; } = "0";
    public string GrayFixedHighText { get; set; } = "65535";
    public string GammaText { get; set; } = "1";
    public string TrainingDataset { get => _trainingDataset; set { if (Set(ref _trainingDataset, value)) ClearTrainingError("Dataset"); } }
    public string TrainingModel { get => _trainingModel; set { if (Set(ref _trainingModel, value)) ClearTrainingError("Model"); } }
    public string Device { get => _device; set { if (Set(ref _device, value)) ClearTrainingError("Settings"); } }
    public string EpochsText { get => _epochs; set { if (Set(ref _epochs, value)) ClearTrainingError("Settings"); } }
    public string BatchText { get => _batch; set { if (Set(ref _batch, value)) ClearTrainingError("Settings"); } }
    public string InputSizeText { get => _inputSize; set { if (Set(ref _inputSize, value)) ClearTrainingError("Settings"); } }
    public string LearningRateText { get => _learningRate; set { if (Set(ref _learningRate, value)) ClearTrainingError("Settings"); } }
    public string Optimizer { get => _optimizer; set { if (Set(ref _optimizer, value)) ClearTrainingError("Settings"); } }
    public IReadOnlyList<string> Optimizers { get; } = ["auto", "SGD", "Adam", "AdamW", "RMSProp", "RAdam", "NAdam", "Adamax", "MuSGD"];
    public bool UseAmp { get; set; } = true;
    public IReadOnlyList<string> TestBackends { get; } = ["onnx", "pt"];
    public string ConfidenceText { get; set; } = "0.25";
    public string IouText { get; set; } = "0.45";
    public string MergeIouText { get; set; } = "0.5";
    public bool TestTiled { get; set; }
    public string ClassIdsText { get; set; } = "";
    public string TestInputsText { get; set; } = "";
    public string EvaluationDataset { get; set; } = "";
    public string CompareOnnxPath { get; set; } = "";
    public string ExportDirectory { get; set; } = "";
    public string ExportModelPath { get; set; } = "";
    public string ExportOpsetText { get; set; } = "17";
    public bool ExportDynamic { get; set; }
    public bool ExportHalf { get; set; }
    public string OutputFolder { get; private set; } = "";
    public string TestPreviewPath { get; private set; } = "";
    public int TestImageWidth { get; private set; }
    public int TestImageHeight { get; private set; }
    public string SlicePreviewPath { get; private set; } = "";
    public int SliceImageWidth { get; private set; }
    public int SliceImageHeight { get; private set; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) Notify(nameof(CanOperate)); } }
    public bool IsConnected { get => _connected; private set { if (Set(ref _connected, value)) { Notify(nameof(CanOperate)); Notify(nameof(ConnectionDescription)); } } }
    public bool CanOperate => IsConnected && !IsBusy;
    public bool HasProject => SelectedProject is not null && IsConnected;
    public bool HasRunningTasks => Tasks.Any(t => t.Active) || TrainingHistory.Any(t => t.Active);
    public string ConnectionDescription => IsConnected ? "AI 工作区已连接" : "AI 工作区未连接 · 普通检测仍可使用";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string Diagnostic { get => _diagnostic; private set => Set(ref _diagnostic, value); }
    public string WorkspaceId => _workspaceId;
    public string PageDescription => $"第 {_page + 1} 页 · 共 {_total} 张 · {Images.Count} 张已加载";
    public string AnnotationState { get => _annotationState; private set => Set(ref _annotationState, value); }
    public string PreviewPath { get => _previewPath; private set => Set(ref _previewPath, value); }
    public int ImageWidth { get => _imageWidth; private set => Set(ref _imageWidth, value); }
    public int ImageHeight { get => _imageHeight; private set => Set(ref _imageHeight, value); }
    public string LogText { get => _log; private set => Set(ref _log, value); }
    public string DeploymentInfo { get => _deploymentInfo; private set => Set(ref _deploymentInfo, value); }
    public string TestSummary { get => _testSummary; private set => Set(ref _testSummary, value); }
    public string AnalysisText { get; private set; } = "尚未计算实际标注统计。";
    public string ResultPageDescription => $"检测结果第 {_resultPage + 1} 页 · 共 {_resultTotal} 张";
    public AiProjectItem? SelectedProject { get => _project; set { if (Set(ref _project, value)) { Notify(nameof(HasProject)); TrainingContextError = ""; _projectChangeTask = RunUiAsync(ChangeProjectAsync); } } }
    public AiImageItem? SelectedImage { get => _image; set { if (Set(ref _image, value)) { SelectedBox = null; NotifyAnnotationAvailability(); _ = RunUiAsync(LoadImageAsync); } } }
    public AiClassItem? SelectedClass { get => _class; set { if (Set(ref _class, value) && value is not null) { ClassName = value.Name; ClassColor = value.Color; Notify(nameof(ClassName)); Notify(nameof(ClassColor)); } } }
    public AiBox? SelectedBox { get => _box; set { if (Set(ref _box, value)) ReloadAnnotationDraft(); } }
    public AiModelItem? SelectedModel { get => _model; set { if (Set(ref _model, value) && value is not null) { ExportModelPath = value.Path; Notify(nameof(TestModelPath)); Notify(nameof(ExportModelPath)); } } }
    public AiTaskItem? SelectedTask { get => _task; set { if (Set(ref _task, value) && value?.Kind == "training") { _trainingTaskId = value.Id; _logOffset = 0; LogText = ""; } } }
    public AiDetectionItem? SelectedDetection { get => _detection; set => Set(ref _detection, value); }
    public AiInferenceImageItem? SelectedInferenceImage { get => _inferenceImage; set { if (Set(ref _inferenceImage, value) && value is not null) DisplayInferenceImage(value); } }
    public Func<JsonElement, Task>? PresentNativeTileAsync { get; set; }

    public Task InitializeAsync() { Status = "AI 项目、Python 环境和数据目录独立配置；连接不会启动训练或下载模型。"; return Task.CompletedTask; }
    public void ApplyTrainingMachineConfiguration(VisionPlatform.Contracts.Deployment.MachineConfiguration configuration)
    {
        if (IsConnected) throw new InvalidOperationException("训练工作区已连接，未改变正在使用的环境。");
        if (!configuration.TrainingEnabled) { Status = "可选训练工作区未启用；核心和独立AI检测可继续使用。已有数据和配置没有删除。"; return; }
        YoloRoot = configuration.TrainingRoot; PythonPath = configuration.TrainingPython; Notify(nameof(YoloRoot)); Notify(nameof(PythonPath));
        Status = "已带入本机训练环境；请显式连接。数据目录保持原值，未访问数据库或启动任务。";
    }
    public async Task RunUiAsync(Func<Task> operation)
    { try { Error = ""; await operation(); } catch (OperationCanceledException) { Status = "操作已取消。"; } catch (Exception ex) { Error = ex.GetBaseException().Message; Status = "操作未完成，请检查提示。"; } }
    private async Task Work(Func<Task> operation)
    { if (IsBusy) return; IsBusy = true; try { await operation(); } finally { IsBusy = false; } }
    private object ProjectPayload() => new { project_id = RequireProject() };
    private string RequireProject() => SelectedProject?.Id ?? throw new InvalidOperationException("请先选择 AI 项目。");
    private async Task<JsonElement> Call(string method, object? payload = null, CancellationToken? cancellation = null)
    { if (!IsConnected && method != "hello") throw new InvalidOperationException("请先连接 AI 工作区。"); return await _client.CallAsync(method, payload, cancellation ?? _lifetime.Token); }

    public Task ConnectAsync() => Work(async () =>
    {
        if (_client.IsRunning) throw new InvalidOperationException("AI 服务已连接。切换数据目录前请关闭当前服务，避免误写其他数据。");
        var settings = new AiServiceSettings { YoloRoot = YoloRoot, PythonPath = PythonPath, DataHome = DataHome, TransferRoot = TransferRoot, ServiceScript = ServiceScript };
        await _client.StartAsync(settings, _lifetime.Token);
        var hello = await _client.CallAsync("hello", null, _lifetime.Token);
        _workspaceId = AiJson.Text(hello, "workspace_id"); Notify(nameof(WorkspaceId)); IsConnected = true;
        Diagnostic = AiJson.Text(hello, "message", "description"); if (Diagnostic.Length == 0) Diagnostic = hello.ToString();
        if (SaveSettingsAsync is not null) await SaveSettingsAsync(settings);
        _poll.Start(); await RefreshProjectsAsync(); Status = "已连接。原 YOLOStudio 的项目和记录通过业务服务访问，未搬移数据。";
    });
    public async Task DiagnoseAsync()
    { var result = await Call("diagnostics", new { include_gpu = true }); Diagnostic = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }); }
    public async Task RefreshProjectsAsync()
    {
        var result = await Call("projects.list"); var previous = SelectedProject?.Id;
        AiJson.Replace(Projects, AiJson.Items(result, "items").Select(p => new AiProjectItem(AiJson.Text(p, "id"), AiJson.Text(p, "name"))));
        if (previous is not null) _project = Projects.FirstOrDefault(p => p.Id == previous);
        Notify(nameof(SelectedProject));
    }
    public async Task CreateProjectAsync()
    { var response = await Call("projects.create", new { name = NewProjectName }); await RefreshProjectsAsync(); SelectedProject = Projects.FirstOrDefault(p => p.Id == AiJson.Text(AiJson.Field(response, "project"), "id")); }
    private async Task ChangeProjectAsync()
    {
        await FlushAnnotationsAsync(); Interlocked.Increment(ref _imageRequest); Interlocked.Increment(ref _tileVersion);
        ResetPreparationContext(); _trainingTaskId = null; _logOffset = 0; Metrics.Clear(); LogText = "";
        var oldPreview = PreviewPath; var oldSlices = SlicePreviewPath; _loadedImage = null; _loadedProjectId = null; _image = null; SelectedBox = null; Notify(nameof(SelectedImage)); Boxes.Clear(); PreviewPath = ""; SlicePreviewPath = ""; Notify(nameof(SlicePreviewPath)); ImageWidth = ImageHeight = 0; Detections.Clear(); _page = 0;
        await ReleaseTransferAsync(oldPreview, oldSlices);
        if (SelectedProject is null || !IsConnected) return;
        var context = await Call("context.select", ProjectPayload());
        var preprocessing = AiJson.Field(AiJson.Field(context, "settings"), "preprocess");
        if (preprocessing.ValueKind == JsonValueKind.Object && preprocessing.EnumerateObject().Any())
        {
            GrayMapping = AiJson.Text(preprocessing, "mode") == "fixed" ? "range" : "percentile";
            GrayFixedLowText = AiJson.Number(preprocessing, "low").ToString("G", CultureInfo.InvariantCulture); GrayFixedHighText = AiJson.Number(preprocessing, "high").ToString("G", CultureInfo.InvariantCulture);
            GrayLowText = AiJson.Number(preprocessing, "percentile_low").ToString("G", CultureInfo.InvariantCulture); GrayHighText = AiJson.Number(preprocessing, "percentile_high").ToString("G", CultureInfo.InvariantCulture); GammaText = AiJson.Number(preprocessing, "gamma").ToString("G", CultureInfo.InvariantCulture);
            foreach (var property in new[] { nameof(GrayMapping), nameof(GrayFixedLowText), nameof(GrayFixedHighText), nameof(GrayLowText), nameof(GrayHighText), nameof(GammaText) }) Notify(property);
        }
        await RefreshClassesAsync(); await RefreshImagesAsync(); await RefreshModelsAsync(); await RefreshHistoryAsync();
        Status = "当前 AI 项目已切换；训练任务和独立检测工程不会被自动替换。";
    }
    public async Task LinkProjectToInspectionAsync()
    { if (LinkProjectAsync is null) throw new InvalidOperationException("宿主尚未配置工程关联命令。"); await LinkProjectAsync(new(RequireProject(), WorkspaceId, DataHome, SelectedProject!.Name)); Status = "AI 项目关联已写入检测工程；AI 数据仍独立保存。"; }
    public async Task RefreshImagesAsync(int pageDelta = 0)
    {
        var project = RequireProject(); _page = Math.Max(0, _page + pageDelta);
        var result = await Call("images.list", new { project_id = project, page = _page, page_size = 60, search = SearchText, filter = ReviewFilter switch { "未检查" => "unreviewed", "已检查" => "reviewed", "正常样本" => "normal", "有标注" => "annotated", _ => "all" } });
        _total = (int)AiJson.Number(result, "total");
        AiJson.Replace(Images, AiJson.Items(result, "items").Select(p => new AiImageItem(AiJson.Text(p, "id"), AiJson.Text(p, "filename", "name"), AiJson.Text(p, "managed_absolute"), AiJson.Text(p, "thumbnail_absolute"), (int)AiJson.Number(p, "width"), (int)AiJson.Number(p, "height"), AiJson.Boolean(p, "reviewed"), AiJson.Text(p, "batch"), (int)AiJson.Number(p, "annotation_count", "box_count"))));
        Notify(nameof(PageDescription));
    }
    public async Task ImportAsync(IEnumerable<string> paths, bool recursive)
    { var result = await Call("images.import", new { project_id = RequireProject(), paths = paths.ToArray(), recursive }); RememberTask(result, "import"); }
    public async Task ReviewAsync(IEnumerable<AiImageItem> items, bool reviewed)
    { await Call("images.review", new { project_id = RequireProject(), image_ids = items.Select(i => i.Id).ToArray(), reviewed }); await RefreshImagesAsync(); }
    public async Task AssignBatchAsync(IEnumerable<AiImageItem> items)
    { await Call("images.batch", new { project_id = RequireProject(), image_ids = items.Select(i => i.Id).ToArray(), batch = BatchName }); await RefreshImagesAsync(); }
    public async Task DeleteImagesAsync(IEnumerable<AiImageItem> items)
    { await FlushAnnotationsAsync(); await Call("images.delete", new { project_id = RequireProject(), image_ids = items.Select(i => i.Id).ToArray(), confirmed = true }); SelectedImage = null; _loadedImage = null; Boxes.Clear(); PreviewPath = ""; await RefreshImagesAsync(); await RefreshClassesAsync(); }
    public async Task RenameProjectAsync()
    { await Call("projects.rename", new { project_id = RequireProject(), name = NewProjectName }); await RefreshProjectsAsync(); }
    public async Task DeleteProjectAsync()
    { var id = RequireProject(); await FlushAnnotationsAsync(); await Call("projects.delete", new { project_id = id, confirmed = true, confirm_project_id = id }); SelectedProject = null; await RefreshProjectsAsync(); }
    public async Task RefreshClassesAsync()
    {
        var result = await Call("classes.list", ProjectPayload()); var selected = SelectedClass?.Id;
        AiJson.Replace(Classes, AiJson.Items(result, "items").Select((p, index) => new AiClassItem((int)AiJson.Number(p, "class_id"), AiJson.Text(p, "name"), AiJson.Text(p, "color"), (int)AiJson.Number(p, "annotation_count"), ClassShortcut(index))));
        SelectedClass = Classes.FirstOrDefault(c => c.Id == selected) ?? Classes.FirstOrDefault();
        RefreshAnnotationLabels();
    }
    public async Task SaveClassAsync(bool create)
    { await Call("classes.save", new { project_id = RequireProject(), class_id = create ? (int?)null : SelectedClass?.Id, name = ClassName, color = ClassColor }); await RefreshClassesAsync(); }
    public async Task TransferLabelsAsync(string path, bool import)
    { await FlushAnnotationsAsync(); var result = await Call(import ? "labels.import" : "labels.export", new { project_id = RequireProject(), path }); RememberTask(result, import ? "labels.import" : "labels.export"); }
    public async Task AnalyzeAsync(string? exportPath = null)
    { var report = await Call("analysis", new { project_id = RequireProject(), export_path = exportPath }); AnalysisText = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }); Notify(nameof(AnalysisText)); Status = "已按真实标注计算尺寸统计；详情可复制或导出CSV。"; }
    private static string ClassShortcut(int index) => index < 40 ? ((index / 10) switch { 1 => "Ctrl+", 2 => "Alt+", 3 => "Ctrl+Alt+", _ => "" }) + (index % 10 == 9 ? "0" : (index % 10 + 1).ToString(CultureInfo.InvariantCulture)) : "Ctrl+K " + (index + 1).ToString(CultureInfo.InvariantCulture);
    public async Task NavigateImageAsync(int delta)
    {
        await FlushAnnotationsAsync(); if (Images.Count == 0) return;
        var current = Array.FindIndex(Images.ToArray(), image => image.Id == SelectedImage?.Id);
        var target = current + delta;
        if (target < 0 && _page > 0) { await RefreshImagesAsync(-1); SelectedImage = Images.LastOrDefault(); }
        else if (target >= Images.Count && (_page + 1) * 60 < _total) { await RefreshImagesAsync(1); SelectedImage = Images.FirstOrDefault(); }
        else if (target >= 0 && target < Images.Count) SelectedImage = Images[target];
    }
    private async Task LoadImageAsync()
    {
        await FlushAnnotationsAsync(); var image = SelectedImage; var project = SelectedProject; var request = Interlocked.Increment(ref _imageRequest); Interlocked.Increment(ref _tileVersion);
        if (image is null || project is null) return;
        var annotations = await Call("annotations.get", new { project_id = project.Id, image_id = image.Id });
        var preview = await Call("preview", new { project_id = project.Id, image_id = image.Id, max_edge = 2048 });
        if (request != _imageRequest || SelectedImage?.Id != image.Id) { await ReleaseTransferAsync(AiJson.Text(preview, "path")); return; }
        var previousPreview = PreviewPath;
        _loadingAnnotations = true;
        try
        {
            _annotationRevision = AiJson.Text(annotations, "revision");
            _loadedImage = image; _loadedProjectId = project.Id;
            AiJson.Replace(Boxes, AiJson.Items(annotations, "boxes").Select(ReadBox)); SelectedBox = null;
            _annotationUndo.Clear(); _annotationDirty = false;
            ImageWidth = (int)AiJson.Number(preview, "width", "original_width"); ImageHeight = (int)AiJson.Number(preview, "height", "original_height"); PreviewPath = AiJson.Text(preview, "path");
            AnnotationState = $"{image.Name} · {ImageWidth} × {ImageHeight} · {Boxes.Count} 个标注 · 已保存";
        }
        finally { _loadingAnnotations = false; }
        RefreshAnnotationLabels(); ReloadAnnotationDraft();
        await ReleaseTransferAsync(previousPreview);
    }
    private static AiBox ReadBox(JsonElement p) => new() { Id = AiJson.Text(p, "id"), ClassId = (int)AiJson.Number(p, "class_id"), X = AiJson.Number(p, "x"), Y = AiJson.Number(p, "y"), Width = AiJson.Number(p, "w", "width"), Height = AiJson.Number(p, "h", "height") };
    public void BeginAnnotationEdit()
    { if (_loadingAnnotations || SelectedImage is null) return; _annotationUndo.Push(Boxes.Select(b => b.Copy()).ToArray()); while (_annotationUndo.Count > 100) { var keep = _annotationUndo.Take(100).Reverse().ToArray(); _annotationUndo.Clear(); foreach (var entry in keep) _annotationUndo.Push(entry); } }
    public void AnnotationEdited()
    {
        if (_loadingAnnotations || SelectedImage is null) return;
        RefreshAnnotationLabels(); if (!AnnotationDraft.HasChanges) AnnotationDraft.Load(SelectedBox);
        _annotationDirty = true; AnnotationState = $"{SelectedImage.Name} · {Boxes.Count} 个标注 · 正在自动保存…";
        _annotationEditSerial++;
        _saveCancellation?.Cancel(); _saveCancellation?.Dispose(); _saveCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); var token = _saveCancellation.Token;
        _saveTask = RunUiAsync(async () => { await Task.Delay(250, token); await SaveAnnotationsCoreAsync(); });
    }
    public async Task FlushAnnotationsAsync()
    { _saveCancellation?.Cancel(); await _saveTask; if (_annotationDirty) await SaveAnnotationsCoreAsync(); }
    private async Task SaveAnnotationsCoreAsync()
    {
        await _annotationSaveGate.WaitAsync(_lifetime.Token);
        try
        {
            var image = _loadedImage; var projectId = _loadedProjectId; if (!_annotationDirty || image is null || projectId is null) return;
            var serial = _annotationEditSerial; var payload = Boxes.Select(b => b.ToPayload()).ToArray(); var revision = _annotationRevision;
            var response = await Call("annotations.save", new { project_id = projectId, image_id = image.Id, boxes = payload, reviewed = true, expected_revision = revision });
            if (_loadedImage?.Id != image.Id) return;
            _annotationRevision = AiJson.Text(response, "revision"); _annotationDirty = serial != _annotationEditSerial;
            AnnotationState = $"{image.Name} · {Boxes.Count} 个标注 · {(_annotationDirty ? "有待保存修改" : "已自动保存")}";
        }
        finally { _annotationSaveGate.Release(); }
    }
    public void UndoAnnotation()
    { if (_annotationUndo.Count == 0) return; AiJson.Replace(Boxes, _annotationUndo.Pop().Select(b => b.Copy())); SelectedBox = null; AnnotationEdited(); }
    public void DeleteAnnotation()
    { if (SelectedBox is not { } box) return; BeginAnnotationEdit(); Boxes.Remove(box); SelectedBox = null; AnnotationEdited(); }
    public async Task RequestNativeTileAsync(double x, double y, double width, double height)
    {
        if (SelectedImage is not { } image || !IsConnected) return;
        var version = Interlocked.Increment(ref _tileVersion);
        await _tileGate.WaitAsync(_lifetime.Token);
        try
        {
            if (version != _tileVersion || SelectedImage?.Id != image.Id) return;
            var result = await Call("tile", new { project_id = RequireProject(), image_id = image.Id, x = Math.Max(0, (int)x), y = Math.Max(0, (int)y), width = Math.Clamp((int)Math.Ceiling(width), 1, 4096), height = Math.Clamp((int)Math.Ceiling(height), 1, 4096) });
            try { if (version == _tileVersion && SelectedImage?.Id == image.Id && PresentNativeTileAsync is { } present) await present(result); }
            finally { await ReleaseTransferAsync(AiJson.Text(result, "path")); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = "原像素局部查看失败：" + ex.GetBaseException().Message; }
        finally { _tileGate.Release(); }
    }
    private async Task ReleaseTransferAsync(params string[] paths)
    {
        var owned = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (owned.Length == 0 || !IsConnected || !_client.IsRunning) return;
        await Call("files.release", new { paths = owned });
    }
    private object Preprocess() => new { mode = GrayMapping == "range" ? "fixed" : "percentile", low = AiJson.Parse(GrayFixedLowText, "固定灰度下限", 0, 65535), high = AiJson.Parse(GrayFixedHighText, "固定灰度上限", 1, 65535), percentile_low = AiJson.Parse(GrayLowText, "灰度低分位", 0, 100), percentile_high = AiJson.Parse(GrayHighText, "灰度高分位", 0, 100), gamma = AiJson.Parse(GammaText, "Gamma", .1, 10) };
    private object DatasetOptions() => new { tiled = DatasetTiled, tile_width = AiJson.Integer(TileWidthText, "切片宽", 16, 8192), tile_height = AiJson.Integer(TileHeightText, "切片高", 16, 8192), overlap = AiJson.Parse(OverlapText, "重叠比例", 0, .99), grouping = Grouping,
        ratios = new[] { AiJson.Parse(TrainRatioText, "训练比例", 0, 1), AiJson.Parse(ValidationRatioText, "验证比例", 0, 1), AiJson.Parse(TestRatioText, "测试比例", 0, 1) }, preprocess = Preprocess(), output_parent = DatasetOutput };
    public async Task PreviewSlicesAsync()
    {
        var tileIndex = AiJson.Integer(TileIndexText, "预览切片编号（-1为网格）", -1, int.MaxValue);
        var response = await Call("slices.preview", new { project_id = RequireProject(), image_id = SelectedImage?.Id ?? throw new InvalidOperationException("请先选择图片。"), options = DatasetOptions(), preprocess = Preprocess(), tile_index = tileIndex });
        var priorSlice = SlicePreviewPath;
        if (AiJson.Text(response, "path", "preview_path") is { Length: > 0 } path) { SlicePreviewPath = path; Notify(nameof(SlicePreviewPath)); }
        await ReleaseTransferAsync(priorSlice);
        var region = AiJson.Field(response, "region");
        SliceImageWidth = (int)(region.ValueKind == JsonValueKind.Object ? AiJson.Number(region, "width") : AiJson.Number(response, "width")); SliceImageHeight = (int)(region.ValueKind == JsonValueKind.Object ? AiJson.Number(region, "height") : AiJson.Number(response, "height")); Notify(nameof(SliceImageWidth)); Notify(nameof(SliceImageHeight));
        AiJson.Replace(SliceBoxes, tileIndex >= 0 ? AiJson.Items(response, "tile_boxes").Select(ReadBox) : AiJson.Items(response, "tiles").Select(t => new AiBox { Id = AiJson.Text(t, "index"), ClassId = -1, X = AiJson.Number(t, "x"), Y = AiJson.Number(t, "y"), Width = AiJson.Number(t, "width"), Height = AiJson.Number(t, "height"), Label = "切片 " + AiJson.Text(t, "index") }));
        Status = AiJson.Text(response, "message"); if (Status.Length == 0) Status = $"实际切片预览已生成，共 {AiJson.Number(response, "tile_count", "count")} 片。";
        DataPreparationStatus = "切片预览已生成；未执行切片任务、未生成训练数据集，也未开始训练。";
    }
    public async Task StartSlicesAsync(bool dataset)
    {
        var projectId = RequireProject();
        var result = await Call(dataset ? "dataset.build" : "slices.run", new { project_id = projectId, options = DatasetOptions() });
        _preparationTaskId = AiJson.Text(result, "task_id"); _preparationProjectId = projectId;
        if (dataset) { _preparedProjectId = null; PreparedDatasetPath = ""; }
        DataPreparationStatus = dataset ? "正在生成可训练数据集；完成后可带入训练页，不会自动训练。" : "正在执行切片任务；片数进度不是训练进度。";
        RememberTask(result, dataset ? "dataset" : "slices");
    }
    public async Task SavePreprocessingAsync() { await Call("preprocessing.save", new { project_id = RequireProject(), preprocess = Preprocess() }); Status = "实际预处理参数已保存至 AI 项目；后续数据准备保留独立快照，未修改原图。"; }
    public async Task RefreshHistoryAsync()
    {
        if (!HasProject) return; var response = await Call("history.list", ProjectPayload());
        var items = AiJson.Items(response, "items");
        AiJson.Replace(History, items.Select(p => $"{AiJson.Text(p, "kind")} · {AiJson.Text(p, "status")} · {AiJson.Text(p, "created_at")} · {AiJson.Text(p, "output_path")}"));
        AiJson.Replace(DatasetPaths, items.Where(p => AiJson.Text(p, "kind") == "dataset" && AiJson.Text(p, "status") == "completed").Select(p => Path.Combine(AiJson.Text(p, "output_path"), "data.yaml")));
        if (_preparationTaskId is null) { _preparedProjectId = SelectedProject?.Id; PreparedDatasetPath = DatasetPaths.FirstOrDefault(File.Exists) ?? ""; }
    }
    public async Task StopTrainingAsync()
    { await Call("training.stop", new { task_id = SelectedTask?.Kind == "training" ? SelectedTask.Id : _trainingTaskId ?? throw new InvalidOperationException("请选择训练任务。") }); Status = "已请求现有训练服务安全停止；正在等待检查点和进程状态。"; }
    private object DetectionConfig(string? model = null) => new { model = model ?? TestModelPath, backend = TestBackend, device = TestDevice, imgsz = AiJson.Integer(TestInputSizeText, "测试输入尺寸", 32, 4096), batch = AiJson.Integer(TestBatchText, "测试批大小", 1, 64), conf = AiJson.Parse(ConfidenceText, "置信度", 0, 1), iou = AiJson.Parse(IouText, "IoU", 0, 1), merge_iou = AiJson.Parse(MergeIouText, "合并IoU", 0, 1), tiled = TestTiled, tile_width = AiJson.Integer(TileWidthText, "切片宽", 16, 8192), tile_height = AiJson.Integer(TileHeightText, "切片高", 16, 8192), overlap = AiJson.Parse(OverlapText, "重叠比例", 0, .99), ng_classes = ClassIdsText, max_det = 300, preprocess = (object?)null };
    public async Task StartInferenceAsync()
    { await ValidateTestBeforeRequestAsync(); var response = await Call("inference.start", new { project_id = RequireProject(), model_path = TestModelPath, config = DetectionConfig(), inputs = TestInputsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }); _inferenceTaskId = AiJson.Text(response, "task_id"); RememberTask(response, "inference"); }
    public async Task StartEvaluationAsync(bool compare)
    { await ValidateTestBeforeRequestAsync(); var response = await Call(compare ? "comparison.start" : "evaluation.start", new { project_id = RequireProject(), model_path = TestModelPath, config = DetectionConfig(), options = new { device = TestDevice, imgsz = AiJson.Integer(TestInputSizeText, "测试输入尺寸", 32, 4096) }, dataset = EvaluationDataset, onnx_model = CompareOnnxPath, image = TestInputsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() }); RememberTask(response, compare ? "comparison" : "evaluation"); }
    public async Task RefreshInferenceResultsAsync(string? task = null, int pageDelta = 0)
    {
        if (task is not null && task != _inferenceTaskId) _resultPage = 0;
        _inferenceTaskId = task ?? _inferenceTaskId; _resultPage = Math.Max(0, _resultPage + pageDelta);
        var response = await Call("inference.results", new { task_id = _inferenceTaskId ?? SelectedTask?.Id ?? throw new InvalidOperationException("请先选择一次检测任务。"), page = _resultPage, page_size = 20 });
        _resultTotal = (int)AiJson.Number(response, "total"); Notify(nameof(ResultPageDescription));
        AiJson.Replace(InferenceImages, AiJson.Items(response, "items", "results").Select(image => new AiInferenceImageItem(AiJson.Text(image, "id", "image_id"), Path.GetFileName(AiJson.Text(image, "source", "source_path")), AiJson.Text(image, "source", "source_path"), AiJson.Text(image, "preview_path", "preview", "annotated_preview"), (int)AiJson.Number(image, "width"), (int)AiJson.Number(image, "height"), AiJson.Text(image, "quality"), AiJson.Number(image, "elapsed_ms"), AiJson.Text(image, "model_sha256"), AiJson.Text(image, "frame_id"), AiJson.Text(image, "source_kind"), AiJson.Text(image, "task_id"), AiJson.Field(image, "boxes").Clone())));
        SelectedInferenceImage = InferenceImages.FirstOrDefault(); if (SelectedInferenceImage is null) { Detections.Clear(); DetectionBoxes.Clear(); TestPreviewPath = ""; Notify(nameof(TestPreviewPath)); TestSummary = "当前检测尚无图像结果。"; }
    }
    private void DisplayInferenceImage(AiInferenceImageItem image)
    {
        var boxes = AiJson.Items(image.Boxes);
        AiJson.Replace(Detections, boxes.Select(p => new AiDetectionItem((int)AiJson.Number(p, "class_id"), AiJson.Text(p, "class_name", "name"), AiJson.Number(p, "confidence"), AiJson.Number(p, "x"), AiJson.Number(p, "y"), AiJson.Number(p, "w"), AiJson.Number(p, "h"))));
        var palette = new[] { "#2C7BE5", "#D94A53", "#238B68", "#D99A21", "#8E62BE", "#19A7A0" };
        AiJson.Replace(DetectionClasses, Detections.GroupBy(d => d.ClassId).Select(g => new AiClassItem(g.Key, g.First().ClassName, Classes.FirstOrDefault(c => c.Id == g.Key && c.Name == g.First().ClassName)?.Color ?? palette[Math.Abs(g.Key) % palette.Length], g.Count())));
        AiJson.Replace(DetectionBoxes, boxes.Select(ReadBox));
        TestImageWidth = image.Width; TestImageHeight = image.Height; TestPreviewPath = image.PreviewPath;
        Notify(nameof(TestImageWidth)); Notify(nameof(TestImageHeight)); Notify(nameof(TestPreviewPath));
        TestSummary = $"{image.Quality} · {Detections.Count} 个目标 · {image.ElapsedMilliseconds:F2} ms · {image.SourcePath}\n模型 SHA256：{image.ModelHash} · 任务：{image.TaskId}\nFrameId：{image.FrameId} · 来源：{image.SourceKind}";
    }
    public async Task RefreshModelsAsync()
    {
        if (!HasProject) return; var response = await Call("models.list", new { project_id = RequireProject(), include_hashes = true });
        var previous = SelectedModel?.Path;
        var models = AiJson.Items(response, "items").Select(p => new AiModelItem(AiJson.Text(p, "id"), AiJson.Text(p, "name"), AiJson.Text(p, "path", "model_path"), AiJson.Text(p, "run_id"), AiJson.Text(p, "created_at"), AiJson.Field(p, "metrics").ToString(), AiJson.Text(p, "sha256", "model_sha256"))).ToList();
        foreach (var export in AiJson.Items(response, "export_runs").Where(p => AiJson.Text(p, "status") == "completed"))
        {
            var summary = AiJson.Field(export, "summary"); var path = AiJson.Text(summary, "onnx");
            if (path.Length > 0) models.Add(new(AiJson.Text(export, "id"), "ONNX · " + Path.GetFileName(path), path, AiJson.Text(export, "id"), AiJson.Text(export, "created_at"), "已通过原服务 Runtime 校验", AiJson.Text(export, "model_sha256")));
        }
        AiJson.Replace(Models, models); if (previous is not null) _model = Models.FirstOrDefault(m => m.Path == previous); Notify(nameof(SelectedModel));
    }
    public async Task ExportModelAsync()
    { var response = await Call("models.export", new { project_id = RequireProject(), model_path = ExportModelPath, options = new { output_dir = ExportDirectory, imgsz = AiJson.Integer(InputSizeText, "输入尺寸", 32, 4096), opset = AiJson.Integer(ExportOpsetText, "Opset", 12, 22), dynamic = ExportDynamic, precision = ExportHalf ? "FP16" : "FP32", device = Device } }); RememberTask(response, "export"); }
    public async Task InspectDeploymentAsync(string? modelPath = null)
    {
        var model = modelPath ?? SelectedModel?.Path ?? TestModelPath;
        _verifiedDeployment = await Call("deployment.inspect", new { project_id = SelectedProject?.Id, model_path = model, image_size = AiJson.Integer(InputSizeText, "输入尺寸", 32, 4096) });
        DeploymentInfo = JsonSerializer.Serialize(_verifiedDeployment, new JsonSerializerOptions { WriteIndented = true }); Status = "模型与部署契约验证完成。应用不会启动检测或替换正在运行的模型。";
    }
    public async Task ApplyModelToNodeAsync(bool create, bool testContext = false)
    {
        var model = testContext ? TestModelPath : SelectedModel?.Path ?? TestModelPath;
        await InspectDeploymentAsync(model); if (ApplyModelAsync is null) throw new InvalidOperationException("宿主尚未配置模型应用命令。");
        await ApplyModelAsync(new AiModelSelection(model, _verifiedDeployment, AiJson.Parse(ConfidenceText, "置信度", 0, 1), AiJson.Parse(IouText, "IoU", 0, 1), AiJson.Integer(InputSizeText, "输入尺寸", 32, 4096), TestTiled, AiJson.Integer(TileWidthText, "切片宽", 16, 8192), AiJson.Integer(TileHeightText, "切片高", 16, 8192), AiJson.Parse(OverlapText, "重叠比例", 0, .99), AiJson.Parse(MergeIouText, "合并IoU", 0, 1), RequireProject(), WorkspaceId, SelectedModel?.Id) { ClassIds = ClassIdsText }, create);
        Status = "已修改检测工程节点参数；请保存工程。未执行检测或发送通信数据。";
    }
    public async Task CancelTaskAsync()
    { var id = SelectedTask?.Id ?? Tasks.FirstOrDefault(t => t.Active)?.Id ?? throw new InvalidOperationException("没有可取消的任务。"); await Call("tasks.cancel", new { task_id = id }); Status = "已请求取消，已完成的数据保留。"; }
    private void RememberTask(JsonElement response, string kind)
    {
        var id = AiJson.Text(response, "task_id", "id");
        if (id.Length == 0) throw new InvalidDataException("业务服务未返回任务身份。");
        var item = new AiTaskItem(id, kind, AiJson.Text(response, "status") is { Length: > 0 } status ? status : "waiting", 0, 0, "等待业务服务实际进度", "");
        Tasks.Insert(0, item); SelectedTask = item; Status = $"任务已提交：{kind} · {id}"; Notify(nameof(HasRunningTasks));
    }
    public async Task PollAsync()
    {
        if (_disposed || !IsConnected || _polling) return; _polling = true;
        try
        {
            var response = await Call("tasks.list"); var tasks = AiJson.Items(response, "items");
            UpdatePreparationStatus(tasks);
            var selectedId = SelectedTask?.Id;
            AiJson.Replace(Tasks, tasks.Select(p => new AiTaskItem(AiJson.Text(p, "task_id", "id"), AiJson.Text(p, "kind"), AiJson.Text(p, "status"), (int)AiJson.Number(AiJson.Field(p, "progress"), "current"), (int)AiJson.Number(AiJson.Field(p, "progress"), "total"), AiJson.Text(AiJson.Field(p, "progress"), "message"), AiJson.Text(p, "error"))));
            _task = Tasks.FirstOrDefault(t => t.Id == selectedId) ?? Tasks.FirstOrDefault(); Notify(nameof(SelectedTask));
            foreach (var task in Tasks.Where(t => !t.Active && _completed.Add(t.Id)))
            {
                if (task.Status == "failed") Error = task.Error;
                if (task.Kind == "inference") { _inferenceTaskId = task.Id; await RefreshInferenceResultsAsync(task.Id); }
                if (HasProject && task.Status == "completed") { await RefreshImagesAsync(); await RefreshModelsAsync(); await RefreshHistoryAsync(); }
            }
            if (HasProject)
            {
                var training = await Call("training.list", ProjectPayload());
                AiJson.Replace(TrainingHistory, AiJson.Items(training, "items").Select(p => new AiTaskItem(AiJson.Text(p, "id", "task_id"), "training", AiJson.Text(p, "status"), (int)AiJson.Number(p, "final_epoch"), (int)AiJson.Number(AiJson.Field(p, "config"), "epochs"), AiJson.Text(p, "model_name"), AiJson.Text(p, "error"))));
            }
            if (_trainingTaskId is { Length: > 0 }) await PollTrainingAsync();
            Notify(nameof(HasRunningTasks));
        }
        catch (Exception ex) { Error = ex.GetBaseException().Message; if (!_client.IsRunning) { IsConnected = false; _poll.Stop(); Status = "AI 服务已退出。普通检测仍可使用；重新连接后可恢复查询已记录任务。"; } }
        finally { _polling = false; }
    }
    private async Task PollTrainingAsync()
    {
        var response = await Call("training.poll", new { project_id = SelectedProject?.Id, task_id = _trainingTaskId, log_offset = _logOffset });
        _logOffset = (long)AiJson.Number(response, "log_offset"); var text = AiJson.Text(response, "log_text");
        if (text.Length > 0) { var combined = LogText + text; LogText = combined.Length > 256000 ? combined[^256000..] : combined; }
        AiJson.Replace(Metrics, AiJson.Items(response, "metrics").Select(p => new AiMetricPoint(AiJson.Number(p, "epoch"), AiJson.Optional(p, "train/box_loss", "box_loss"), AiJson.Optional(p, "train/cls_loss", "cls_loss"), AiJson.Optional(p, "metrics/precision(B)", "precision"), AiJson.Optional(p, "metrics/recall(B)", "recall"), AiJson.Optional(p, "metrics/mAP50(B)", "map50"), AiJson.Optional(p, "metrics/mAP50-95(B)", "map5095", "map"))));
        var selected = AiJson.Field(response, "selected"); OutputFolder = AiJson.Text(selected, "output_dir"); Notify(nameof(OutputFolder));
    }
    private void ServiceEvent(object? sender, AiServiceEvent e)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (e.Kind == "log") { var message = AiJson.Text(e.Data, "message", "text"); if (message.Length > 0) { var combined = LogText + message + Environment.NewLine; LogText = combined.Length > 256000 ? combined[^256000..] : combined; } }
            if (e.Kind == "progress") Status = AiJson.Text(e.Data, "message", "text");
        });
    }
    public async Task<bool> PrepareCloseAsync(Func<string, Task<AiCloseChoice>> confirm)
    {
        await FlushAnnotationsAsync(); if (!IsConnected) return true; await PollAsync();
        if (!HasRunningTasks) return true;
        var choice = await confirm("AI 工作区仍有运行中的任务。停止并关闭会请求安全取消；继续后台训练会保留可恢复的训练记录，其他数据任务会先取消。");
        if (choice == AiCloseChoice.Cancel) return false;
        foreach (var task in Tasks.Where(t => t.Active && (choice == AiCloseChoice.StopTasks || t.Kind != "training")).ToArray()) await Call("tasks.cancel", new { task_id = task.Id });
        if (choice == AiCloseChoice.StopTasks)
            foreach (var task in TrainingHistory.Where(t => t.Active).ToArray()) await Call("training.stop", new { task_id = task.Id });
        await Call("shutdown", new { stop_training = choice == AiCloseChoice.StopTasks, detach_training = choice == AiCloseChoice.ContinueTraining });
        _poll.Stop(); IsConnected = false; return true;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _poll.Stop(); _client.EventReceived -= ServiceEvent;
        _saveCancellation?.Cancel(); _lifetime.Cancel();
        try { await _saveTask; } catch (OperationCanceledException) { }
        _saveCancellation?.Dispose(); _lifetime.Dispose();
        await _client.DisposeAsync();
    }
}

using VisionPlatform.Contracts.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Algorithms.ROI;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Acquisition;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Persistence;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Session;

/// <summary>Authoring/session ownership and scheduling. Every algorithm executes through RuntimeExecutor.</summary>
public sealed partial class EditorSession : ObservableObject, IEditorSession
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private RuntimeExecutor _executor;
    private RuntimeExecutionSession _executionSession = new();
    private readonly ProjectStore _store;
    private readonly Dictionary<string, string> _installedAlgorithms;
    private ProjectDocument _document = new();
    private ImageFrame? _sourceImage, _displayImage;
    private RunResult? _retainedRun;
    private readonly List<RunResult> _supportingRuns = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, object>> _outputs = new(StringComparer.Ordinal);
    private CancellationTokenSource? _runCancellation;
    private EditorNode? _selectedNode;
    private OutputInspection? _selectedOutputInspection;
    private IReadOnlyList<OverlayItem> _overlays = [];
    private IReadOnlyList<MeasurementOverlay> _measurementOverlays = [];
    private IReadOnlyList<RecognitionOverlay> _recognitionOverlays = [];
    private RotatedRectRoi? _editedRoi;
    private string _projectName = "新建工程", _status = "就绪", _resourceRoot;
    private string? _projectPath;
    private bool _busy, _hasChanges, _blocking, _disposed;
    private long _revision, _requests;
    private bool _pendingRoiBinding;
    private readonly Dictionary<(string Node, string Port), Pose2D> _bindingPoses = [];
    private readonly Dictionary<(string Node, string Port), string> _bindingImageSpaces = [];

    private long _acquisitionRevision;
    public CameraSession CameraSession => VisionPlatform.Acquisition.CameraSession.For(_executionSession);
    public long AcquisitionRevision => _acquisitionRevision;
    public NodeRegistry Registry { get; }
    public NodeAuthoringCatalog Catalog { get; } = new();
    public ObservableCollection<EditorNode> Nodes { get; } = [];
    public ObservableCollection<EditorConnection> Connections { get; } = [];
    public ObservableCollection<NodeResultRow> Results { get; } = [];
    public ObservableCollection<OutputValueRow> SelectedOutputs { get; } = [];
    public EditorNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value is not null && !Nodes.Contains(value)) throw new ArgumentException("节点不属于当前工程。");
            if (!Set(ref _selectedNode, value)) return;
            RefreshSelectedOutputs();
        }
    }
    public ImageFrame? DisplayImage => _displayImage;
    public InspectionReport? CurrentInspectionReport => SelectedOutputs.Select(o => o.Value).OfType<InspectionReport>().FirstOrDefault();
    public OutputInspection? SelectedOutputInspection => _selectedOutputInspection;
    public IReadOnlyList<OverlayItem> Overlays => _overlays;
    public IReadOnlyList<MeasurementOverlay> MeasurementOverlays => _measurementOverlays;
    public IReadOnlyList<RecognitionOverlay> RecognitionOverlays => _recognitionOverlays;
    /// <summary>A detached dictionary shape with borrowed values from the current execution only.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>> CurrentOutputs => new ReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>(
        _outputs.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, object>)new ReadOnlyDictionary<string, object>(p.Value.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal)), StringComparer.Ordinal));
    public RotatedRectRoi? EditedRoi => _editedRoi;
    public string ProjectName
    {
        get => _projectName;
        set { CheckAlive(); if (_blocking) return; if (Set(ref _projectName, value)) Invalidate("工程名称已修改。"); }
    }
    public string? ProjectPath => _projectPath;
    public string ResourceRoot => _resourceRoot;
    public string StatusMessage => _status;
    public bool IsBusy => _busy;
    public bool HasChanges => _hasChanges;
    public long Revision => _revision;
    public long ResultImageBudgetBytes { get; }
    public long RetainedImageBytes
    {
        get
        {
            var handles = new HashSet<FrameHandle>();
            if (_sourceImage is { IsDisposed: false }) handles.Add(_sourceImage.BufferHandle);
            foreach (var frame in _outputs.Values.SelectMany(v => v.Values).OfType<ImageFrame>())
                if (!frame.IsDisposed) handles.Add(frame.BufferHandle);
            foreach (var frame in _supportingRuns.SelectMany(r => r.Nodes.Values).SelectMany(n => n.Outputs.Values).OfType<ImageFrame>())
                if (!frame.IsDisposed) handles.Add(frame.BufferHandle);
            foreach (var frame in _outputs.Values.SelectMany(v => v.Values).OfType<IterationTraceStore>().SelectMany(t => t.Traces).SelectMany(t => t.Nodes).SelectMany(n => n.Outputs.Values).OfType<ImageFrame>())
                if (!frame.IsDisposed) handles.Add(frame.BufferHandle);
            return handles.Sum(h => h.Length);
        }
    }

    public EditorSession() : this(BuiltinRegistration.CreateRegistry()) { }
    public EditorSession(NodeRegistry registry, long resultImageBudgetBytes = 512L * 1024 * 1024)
    {
        if (resultImageBudgetBytes < 1) throw new ArgumentOutOfRangeException(nameof(resultImageBudgetBytes));
        Registry = registry; ResultImageBudgetBytes = resultImageBudgetBytes;
        _executor = new(registry, logger: null, services: _executionSession);
        var matcher = new CvInspectTemplateMatcher();
        _installedAlgorithms = new(StringComparer.Ordinal) { [matcher.AlgorithmId] = matcher.Version };
        _store = new(registry, _installedAlgorithms);
        _resourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionStudio", "Unsaved", Guid.NewGuid().ToString("N"));
    }

    public void AddNode(string typeId, double x = 0, double y = 0)
    {
        CheckEditable();
        using var edit = BeginEdit("添加节点");
        var definition = Catalog.Get(typeId); var descriptor = Registry.Create(typeId).Descriptor;
        var id = typeId.ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N")[..8];
        var node = new EditorNode(new(id, typeId, definition.DisplayName, definition.DefaultParameters, descriptor.Version), descriptor, x, y);
        Nodes.Add(node); Invalidate("节点已添加，旧结果已失效。"); SelectedNode = node;
        if (typeId == "TemplateMatching") foreach (var pair in _installedAlgorithms) _document.AlgorithmVersion[pair.Key] = pair.Value;
    }
    public void MoveNode(EditorNode node, double x, double y)
    {
        CheckAlive(); if (_blocking || !Nodes.Contains(node)) return;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        using var edit = BeginEdit("移动节点");
        node.X = x; node.Y = y; SetChanges(true);
    }
    public void RemoveNode(EditorNode node)
    {
        CheckEditable(); if (!Nodes.Contains(node)) return;
        using var edit = BeginEdit("删除节点");
        foreach (var edge in Connections.Where(e => e.Source.Node == node || e.Target.Node == node).ToArray()) Connections.Remove(edge);
        Nodes.Remove(node); if (SelectedNode == node) SelectedNode = Nodes.FirstOrDefault();
        Invalidate("节点已删除，旧结果已失效。");
    }
    public bool TryConnect(EditorPort source, EditorPort target, out string? reason)
    {
        CheckEditable();
        if (!source.Node.IsEnabled || !target.Node.IsEnabled)
        { reason = "已禁用节点不能连接端口，请先启用节点并手动连线。"; return false; }
        if (source.IsInput || !target.IsInput || !Nodes.Contains(source.Node) || !Nodes.Contains(target.Node))
        { reason = "连接必须从当前工程的输出端口指向输入端口。"; return false; }
        if (!source.Node.AllOutputs.Contains(source) || !target.Node.AllInputs.Contains(target))
        { reason = "连接端口不存在。"; return false; }
        var edge = new EditorConnection(source, target);
        var candidate = new NodeGraph(Nodes.Select(n => n.Specification), Connections.Select(c => c.ToContract()).Append(edge.ToContract()));
        var errors = new GraphValidator(Registry).Validate(candidate, false)
            .Where(error => !(error.StartsWith("Required input '", StringComparison.Ordinal) && error.EndsWith("' is not connected.", StringComparison.Ordinal))).ToArray();
        if (errors.Length != 0) { reason = string.Join("; ", errors); return false; }
        using var edit = BeginEdit("建立连接");
        Connections.Add(edge); Invalidate("连接已建立，旧结果已失效。"); reason = null; return true;
    }
    public void Disconnect(EditorConnection connection)
    { CheckEditable(); using var edit = BeginEdit("断开连接"); if (Connections.Remove(connection)) Invalidate("连接已断开，旧结果已失效。"); }
    public void CommitParameters(EditorNode node, JsonElement parameters)
    {
        CheckEditable(); if (!Nodes.Contains(node)) throw new ArgumentException("节点不属于当前工程。");
        var errors = Registry.Create(node.TypeId).ValidateParameters(parameters);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors));
        using var edit = BeginEdit("应用参数");
        node.ApplySpecification(node.Specification with { Parameters = parameters.Clone() });
        if (node.TypeId == "ROITransform")
        {
            var roi = parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!.ReferenceROI;
            SetEditedRoi(roi); SetFormalRoi(roi);
        }
        else if (node.TypeId is "CropImage" or "RotatedCrop")
        {
            var roi = CropParameterRoi(node);
            // A downstream crop may use a generated local space. Its numeric parameters already
            // persist in the formal node; do not relabel them as an original-image editable ROI.
            if (_sourceImage is null || CropInputSpace(node) == _sourceImage.CoordinateSpace.Id)
            { SetEditedRoi(roi); SetFormalRoi(roi); }
        }
        if (node.TypeId == "ImageInput") ReleaseSource();
        Invalidate("参数已应用，旧结果已失效。");
        if (node.TypeId == "ImageInput") SetStatus("图像路径或格式已修改；请运行或重新导入以加载新输入，旧预览已清除。");
    }
    public void UpdateRoi(RotatedRectRoi roi)
    {
        CheckEditable(); roi.Validate();
        using var edit = BeginEdit("编辑 ROI");
        if (_sourceImage is not null && roi.CoordinateSpaceId != _sourceImage.CoordinateSpace.Id)
            throw new InvalidOperationException("ROI 编辑使用原始图像坐标；请先选择原图，再绘制和绑定。裁切图的局部坐标需通过 CoordinateTransform 显式映射。");
        if (_displayImage is not null) RoiTransformer.ValidateInImage(roi, _displayImage);
        if (_outputs.Count != 0)
        {
            _bindingPoses.Clear(); _bindingImageSpaces.Clear();
            foreach (var (id, outputs) in _outputs)
                foreach (var (port, value) in outputs)
                {
                    if (value is Pose2D pose) _bindingPoses[(id, port)] = pose;
                    if (value is ImageFrame image) _bindingImageSpaces[(id, port)] = image.CoordinateSpace.Id;
                }
        }
        _pendingRoiBinding = true;
        SetEditedRoi(roi); SetFormalRoi(roi);
        if (SelectedNode is { TypeId: "ROITransform" } roiNode)
        {
            var p = roiNode.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!;
            roiNode.ApplySpecification(roiNode.Specification with { Parameters = JsonDefaults.Element(p with { ReferenceROI = roi }) });
        }
        Invalidate("ROI 已保存到工程数据；可绑定到选定节点。", preserveRoiDraft: true);
    }
    public void BindRoiToSelectedNode()
    {
        CheckEditable(); var roi = EditedRoi ?? throw new InvalidOperationException("请先绘制 ROI。");
        var node = SelectedNode ?? throw new InvalidOperationException("请先选择 ROI 跟随或模板匹配节点。");
        using var edit = BeginEdit("绑定 ROI");
        if (node.TypeId == "ROITransform")
        {
            var p = node.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)!;
            var poseEdge = Connections.FirstOrDefault(e => e.Target.Node == node && e.Target.Name == "Pose2D");
            Pose2D? pose = null;
            if (poseEdge is not null)
            {
                if (_outputs.TryGetValue(poseEdge.Source.Node.NodeId, out var values) && values.TryGetValue(poseEdge.Source.Name, out var value) && value is Pose2D actual) pose = actual;
                else _bindingPoses.TryGetValue((poseEdge.Source.Node.NodeId, poseEdge.Source.Name), out pose);
            }
            if (pose is null) throw new InvalidOperationException("请先成功运行上游模板定位，再绘制并绑定当前 ROI；旧图像或失配姿态不能用作教学参考。");
            node.ApplySpecification(node.Specification with { Parameters = JsonDefaults.Element(p with { ReferenceROI = roi, ReferencePose = pose }) });
        }
        else if (node.TypeId == "TemplateMatching")
        {
            if (Math.Abs(roi.AngleDegrees) > 1e-8) throw new NotSupportedException("模板搜索窗口目前要求轴对齐矩形；旋转 ROI 可绑定到 ROI 跟随节点。");
            var p = node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!;
            node.ApplySpecification(node.Specification with { Parameters = JsonDefaults.Element(p with
            { Options = p.Options with { SearchRoi = new(roi.CenterX - roi.Width / 2, roi.CenterY - roi.Height / 2, roi.Width, roi.Height) } }) });
        }
        else if (node.TypeId == "CropImage")
        {
            RequireCropInputSpace(node, roi);
            if (Math.Abs(roi.AngleDegrees) > 1e-8) throw new NotSupportedException("普通裁切只接受轴对齐 ROI；旋转 ROI 请绑定 RotatedCrop。");
            if (Connections.Any(e => e.Target.Node == node && e.Target.Name == "CropRectangle"))
                throw new InvalidOperationException("CropRectangle 已由端口连接提供；请先断开连接再绑定参数 ROI。");
            var p = node.Parameters.Deserialize<CropImageOptions>(JsonDefaults.Options)!;
            node.ApplySpecification(node.Specification with { Parameters = JsonDefaults.Element(p with
            { X = IntegerRoi(roi.CenterX - roi.Width / 2), Y = IntegerRoi(roi.CenterY - roi.Height / 2), Width = IntegerRoi(roi.Width), Height = IntegerRoi(roi.Height) }) });
        }
        else if (node.TypeId == "RotatedCrop")
        {
            RequireCropInputSpace(node, roi);
            if (Connections.Any(e => e.Target.Node == node && e.Target.Name is "ROI" or "Pose2D"))
                throw new InvalidOperationException("旋转裁切几何已由 ROI/Pose2D 连接提供；请先断开连接再绑定参数 ROI。");
            var p = node.Parameters.Deserialize<RotatedCropOptions>(JsonDefaults.Options)!;
            node.ApplySpecification(node.Specification with { Parameters = JsonDefaults.Element(p with
            { CenterX = roi.CenterX - .5, CenterY = roi.CenterY - .5, Width = IntegerRoi(roi.Width), Height = IntegerRoi(roi.Height), Angle = roi.AngleDegrees }) });
        }
        else if (node.TypeId is "OCR" or "BarcodeReader" or "QRCodeReader" or "YoloInference")
        {
            RequireCropInputSpace(node, roi);
            if (Math.Abs(Math.IEEERemainder(roi.AngleDegrees, 180)) > 1e-8)
                throw new NotSupportedException("识别节点仅直接接受轴对齐 ROI；旋转 ROI 请先使用 RotatedCrop。");
            if (Connections.Any(e => e.Target.Node == node && e.Target.Name == "ROI"))
                throw new InvalidOperationException("ROI 已由输入端口提供，请先断开连接再绑定参数 ROI。");
            var values = node.Parameters.Deserialize<Dictionary<string, JsonElement>>(JsonDefaults.Options)!;
            values["UseRoi"] = JsonDefaults.Element(true);
            values["RoiX"] = JsonDefaults.Element(roi.CenterX - roi.Width / 2);
            values["RoiY"] = JsonDefaults.Element(roi.CenterY - roi.Height / 2);
            values["RoiWidth"] = JsonDefaults.Element(roi.Width); values["RoiHeight"] = JsonDefaults.Element(roi.Height);
            var parameters = JsonDefaults.Element(values);
            var errors = Registry.Create(node.TypeId).ValidateParameters(parameters);
            if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
            node.ApplySpecification(node.Specification with { Parameters = parameters });
        }
        else throw new InvalidOperationException("请选择 ROI 跟随、模板匹配、裁切或识别节点进行绑定。");
        SetFormalRoi(roi); Invalidate("ROI 和参考模板姿态已绑定并写入正式参数。");
    }
    public void SelectOutput(OutputValueRow output)
    {
        if (!SelectedOutputs.Any(row => ReferenceEquals(row, output))) return;
        _selectedOutputInspection = OutputInspector.Describe(output); Notify(nameof(SelectedOutputInspection));
        if (output.Value is ImageFrame frame && !frame.IsDisposed) { SetDisplay(frame); RestoreEditableRoi(); BuildOverlays(); }
        if (output.Value is VisionCollection or CollectionDisplayResult) RefreshCollectionDisplay(output.Value);
        SetStatus(output.Label);
    }
    public bool InspectOutput(string nodeId, string portName)
    {
        var node = Nodes.FirstOrDefault(n => n.NodeId == nodeId); if (node is null) return false;
        SelectedNode = node;
        var output = SelectedOutputs.FirstOrDefault(o => o.PortName == portName);
        if (output is not null) { SelectOutput(output); return true; }
        _selectedOutputInspection = null; Notify(nameof(SelectedOutputInspection));
        SetStatus($"{node.Name}.{portName} 没有本次有效数据（{node.Status}）。请运行节点；未选分支不会复用旧值。"); return false;
    }
    public void SetStatus(string message) { if (Set(ref _status, message, nameof(StatusMessage))) { } }
    public void Cancel()
    { _runCancellation?.Cancel(); if (IsBusy && !_disposed) SetStatus("正在取消，等待当前运算安全结束…"); }

    public async Task NewAsync()
    {
        var request = BeginRequest(); await _operationGate.WaitAsync();
        try
        {
            CheckAlive(); SetBusy(true, true);
            if (request != _requests) return;
            await ResetExecutionServicesAsync();
            Invalidate("已创建新工程。"); ReleaseSource();
            _document = new(); _projectName = "新建工程"; _projectPath = null;
            _resourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VisionStudio", "Unsaved", Guid.NewGuid().ToString("N"));
            ReplaceGraph(_document, null); SetEditedRoi(null); NotifyProjectProperties(); SetChanges(false);
            ResetHistory();
        }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    public Task OpenAsync(string path) => OpenInternalAsync(path, true);
    public Task OpenConfigurationAsync(string path) => OpenInternalAsync(path, false);
    private async Task OpenInternalAsync(string path, bool loadImagePreview)
    {
        var request = BeginRequest(); await _operationGate.WaitAsync();
        try { CheckAlive(); SetBusy(true, true); await LoadCoreAsync(Path.GetFullPath(path), request, loadImagePreview); }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    private async Task LoadCoreAsync(string path, long request, bool loadImagePreview = true)
    {
        var document = await Task.Run(() => _store.LoadAsync(path));
        var root = Path.GetDirectoryName(path)!;
        var inputNode = document.Nodes.FirstOrDefault(n => n.NodeTypeId == "ImageInput" && n.IsEnabled);
        var input = inputNode?.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options);
        var suppliedInput = inputNode is not null && document.Connections.Any(e => e.TargetNodeId == inputNode.NodeId && e.TargetPort == "Image");
        ImageFrame? image = null; EditorLayout? layout = null; string? layoutError = null;
        try
        {
            if (loadImagePreview && !suppliedInput && input is { Path.Length: > 0 }) image = await Task.Run(() => ImageFile.Load(ProjectStore.ResolveResourcePath(root, input.Path), input.PixelFormat));
            try { layout = await EditorLayoutStore.LoadAsync(path, document.ProjectId); }
            catch (InvalidDataException ex) { layoutError = ex.Message; }
            if (_disposed || request != _requests) return;
            await ResetExecutionServicesAsync();
            Invalidate("工程已打开。"); ReleaseSource();
            _document = document; _projectName = document.ProjectName; _projectPath = path; _resourceRoot = root;
            _sourceImage = image; image = null; ReplaceGraph(document, layout);
            SetEditedRoi(document.ROI.FirstOrDefault()); SetDisplay(_sourceImage); SetChanges(false);
            NotifyProjectProperties(); BuildOverlays();
            ResetHistory();
            SetStatus(layoutError is null ? "工程已打开；使用相同 Runtime 执行。" : "工程已打开，布局已重置：" + layoutError);
        }
        finally { image?.Dispose(); }
    }
    public async Task SaveAsync(string? path = null)
    {
        Cancel(); await _operationGate.WaitAsync();
        try
        {
            CheckAlive(); SetBusy(true, true);
            var target = Path.GetFullPath(path ?? ProjectPath ?? throw new InvalidOperationException("请选择工程保存路径。"));
            var root = Path.GetDirectoryName(target)!;
            var snapshot = Rebase(SnapshotProject(), ResourceRoot, root);
            ValidateFileReferences(snapshot, root);
            await Task.Run(() => _store.SaveAsync(target, snapshot));
            _document = snapshot; _projectPath = target; _resourceRoot = root;
            foreach (var spec in snapshot.Nodes) Nodes.Single(n => n.NodeId == spec.NodeId).ApplySpecification(spec);
            await EditorLayoutStore.SaveAsync(target, new(1, snapshot.ProjectId, Nodes.Select(n => new EditorNodeLayout(n.NodeId, n.X, n.Y)).ToArray()));
            NotifyProjectProperties(); SetChanges(false); SetStatus("工程和编辑器布局已保存。");
        }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    public async Task ImportImageAsync(string path, PixelFormat? format = null)
    {
        using var edit = BeginEdit("导入图像");
        var request = BeginRequest(); await _operationGate.WaitAsync(); ImageFrame? image = null;
        try
        {
            CheckAlive(); SetBusy(true, true); var absolute = Path.GetFullPath(path);
            image = await Task.Run(() => ImageFile.Load(absolute, format));
            if (_disposed || request != _requests) return;
            Invalidate("图像已导入，旧结果已失效。"); ReleaseSource();
            _sourceImage = image; image = null;
            var input = Nodes.FirstOrDefault(n => n.TypeId == "ImageInput");
            if (input is null)
            {
                var descriptor = Registry.Create("ImageInput").Descriptor;
                input = new(new("image", "ImageInput", "图像输入", JsonDefaults.Element(new ImageInputParameters { Path = absolute, PixelFormat = format }), descriptor.Version), descriptor);
                Nodes.Add(input);
            }
            else input.ApplySpecification(input.Specification with { Parameters = JsonDefaults.Element(new ImageInputParameters { Path = absolute, PixelFormat = format }) });
            SetDisplay(_sourceImage); SelectedNode = input; Notify(nameof(RetainedImageBytes));
        }
        finally { image?.Dispose(); SetBusy(false, false); _operationGate.Release(); }
    }
    public async Task CreateDemoAsync(string directory)
    {
        var request = BeginRequest(); await _operationGate.WaitAsync();
        try
        {
            CheckAlive(); SetBusy(true, true); var root = Path.GetFullPath(directory); var path = Path.Combine(root, "flow-b.json");
            await Task.Run(async () => { DemoProjectFactory.CreateResources(root); await _store.SaveAsync(path, DemoProjectFactory.Create(true)); });
            if (request == _requests) await LoadCoreAsync(path, request);
        }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    public async Task RunAsync(EditorRunMode mode = EditorRunMode.All)
    {
        var request = _requests;
        Cancel(); await _operationGate.WaitAsync();
        SessionExecution? execution = null;
        try
        {
            CheckAlive(); if (request != _requests) return; SetBusy(true, false);
            var project = SnapshotProject(); var selected = SelectedNode?.NodeId; var revision = Revision; var root = ResourceRoot;
            if (mode != EditorRunMode.All && selected is null) throw new InvalidOperationException("请选择需要运行的节点。");
            if (mode != EditorRunMode.All && SelectedNode is { IsEnabled: false }) throw new InvalidOperationException("已禁用节点不能运行，请先启用并手动恢复连线。");
            ClearResults(); SetStatus("正在后台执行…");
            _bindingPoses.Clear(); _bindingImageSpaces.Clear();
            using var cancellation = new CancellationTokenSource(); _runCancellation = cancellation;
            execution = await Task.Run(async () => mode switch
            {
                EditorRunMode.All => new SessionExecution(await _executor.ExecuteAsync(project.ToGraph(), root, cancellation.Token), []),
                EditorRunMode.ToSelected => new SessionExecution(await _executor.ExecuteToAsync(DependencyGraph(project, selected!), selected!, root, cancellation.Token), []),
                EditorRunMode.Single => await ExecuteSingleFreshAsync(project, selected!, root, cancellation.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            });
            _runCancellation = null;
            if (_disposed || revision != Revision || cancellation.IsCancellationRequested)
            { SetStatus(_disposed ? "会话已关闭。" : "运行已取消或工程已变化，结果未发布。"); return; }
            Publish(execution); execution = null;
        }
        finally
        {
            _runCancellation = null; execution?.Dispose(); SetBusy(false, false); _operationGate.Release();
        }
    }
    private async Task<SessionExecution> ExecuteSingleFreshAsync(ProjectDocument project, string selected, string root, CancellationToken ct)
    {
        var snapshots = new List<RunResult>();
        try
        {
            var inputs = new Dictionary<string, object>(StringComparer.Ordinal);
            var incoming = project.Connections.Where(e => e.TargetNodeId == selected).ToArray();
            if (incoming.Length > 0)
            {
                var dependency = DependencyGraph(project, selected);
                var prefixGraph = new NodeGraph(dependency.Nodes.Where(n => n.NodeId != selected), dependency.Connections.Where(e => e.TargetNodeId != selected));
                var prefix = await _executor.ExecuteAsync(prefixGraph, root, ct); snapshots.Add(prefix);
                var descriptor = Registry.Create(project.Nodes.Single(n => n.NodeId == selected).NodeTypeId).Descriptor;
                var ports = NodePorts.Inputs(descriptor).ToDictionary(p => p.Name);
                object? Read(NodeConnection edge)
                {
                    var source = prefix.Nodes[edge.SourceNodeId];
                    if (source.Status != NodeExecutionStatus.Success) return null;
                    if (edge.SourcePort == NodePorts.Completed) return ControlSignal.Active;
                    return source.Outputs.GetValueOrDefault(edge.SourcePort);
                }
                var controlEdges = incoming.Where(e => ports[e.TargetPort].Kind == PortKind.Control).ToArray();
                foreach (var edge in controlEdges)
                    if (Read(edge) is ControlSignal signal) inputs[edge.TargetPort] = signal;
                    else if (prefix.Nodes[edge.SourceNodeId] is { Status: NodeExecutionStatus.Success } or { SkipReason: NodeSkipReason.InactiveBranch })
                        inputs[edge.TargetPort] = ControlSignal.Inactive;
                var closed = inputs.TryGetValue(NodePorts.Execute, out var gate) && gate is ControlSignal { IsActive: false };
                var merge = Registry.Create(project.Nodes.Single(n => n.NodeId == selected).NodeTypeId) as IExclusiveBranchMergeNode;
                foreach (var edge in incoming.Where(e => ports[e.TargetPort].Kind == PortKind.Data))
                {
                    if (closed) break;
                    if (merge?.Branches.FirstOrDefault(b => b.ValuePort == edge.TargetPort) is { } branch &&
                        inputs.GetValueOrDefault(branch.ControlPort) is ControlSignal { IsActive: false }) continue;
                    if (Read(edge) is { } value) inputs.Add(edge.TargetPort, value);
                    else if (prefix.Nodes[edge.SourceNodeId].SkipReason == NodeSkipReason.InactiveBranch)
                    { inputs[NodePorts.Execute] = ControlSignal.Inactive; closed = true; }
                    else throw new InvalidOperationException($"单节点输入 {edge.SourceNodeId}.{edge.SourcePort} 本次不可用：{prefix.Nodes[edge.SourceNodeId].ErrorMessage}");
                }
                if (!closed)
                    foreach (var edge in controlEdges.Where(e => !inputs.ContainsKey(e.TargetPort)))
                        throw new InvalidOperationException($"控制来源 {edge.SourceNodeId} 本次未成功：{prefix.Nodes[edge.SourceNodeId].ErrorMessage}");
            }
            var result = await _executor.ExecuteSingleAsync(project.Nodes.Single(n => n.NodeId == selected), inputs, root, ct);
            var execution = new SessionExecution(result, snapshots.ToArray()); snapshots.Clear(); return execution;
        }
        finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
    }
    public async Task TeachTemplateAsync()
    {
        using var edit = BeginEdit("教学模板");
        Cancel(); await _operationGate.WaitAsync();
        try
        {
            CheckAlive(); SetBusy(true, true);
            var node = SelectedNode is { TypeId: "TemplateMatching" } selected ? selected : throw new InvalidOperationException("请选择模板匹配节点。");
            var roi = EditedRoi ?? throw new InvalidOperationException("请先绘制模板区域。");
            if (Math.Abs(roi.AngleDegrees) > 1e-8) throw new NotSupportedException("第一轮模板教学要求轴对齐矩形；旋转 ROI 可用于检测跟随。");
            using var fallback = DisplayImage?.Share();
            var project = SnapshotProject(); var imageEdge = project.Connections.FirstOrDefault(e => e.TargetNodeId == node.NodeId && e.TargetPort == "Image");
            var templatePath = Path.Combine(ResourceRoot, "resources", "template-" + Guid.NewGuid().ToString("N") + ".json");
            var root = ResourceRoot;
            await Task.Run(async () =>
            {
                using var upstream = imageEdge is null ? null : await _executor.ExecuteToAsync(DependencyGraph(project, imageEdge.SourceNodeId), imageEdge.SourceNodeId, root);
                var image = imageEdge is null ? fallback ?? throw new InvalidOperationException("请先导入教学图像。")
                    : upstream!.Nodes[imageEdge.SourceNodeId].Output<ImageFrame>(imageEdge.SourcePort);
                if (image.PixelFormat.IsBayer()) throw new NotSupportedException("Bayer 教学图请先通过显式像素格式转换节点。");
                RoiTransformer.ValidateInImage(roi, image);
                var x = (int)Math.Floor(roi.CenterX - roi.Width / 2 - image.CoordinateSpace.OriginX);
                var y = (int)Math.Floor(roi.CenterY - roi.Height / 2 - image.CoordinateSpace.OriginY);
                var right = (int)Math.Ceiling(roi.CenterX + roi.Width / 2 - image.CoordinateSpace.OriginX);
                var bottom = (int)Math.Ceiling(roi.CenterY + roi.Height / 2 - image.CoordinateSpace.OriginY);
                if (right - x < 4 || bottom - y < 4) throw new ArgumentException("模板区域至少需要 4×4 像素。");
                using var crop = image.CreateView(x, y, right - x, bottom - y);
                using var model = new TemplateModel(crop, node.NodeId);
                TemplateResourceStore.Save(templatePath, model);
            });
            var p = node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!;
            node.ApplySpecification(node.Specification with { Parameters = JsonDefaults.Element(p with { TemplatePath = templatePath }) });
            _document.TemplateResources.RemoveAll(r => r.ResourceId == node.NodeId);
            _document.TemplateResources.Add(new(node.NodeId, templatePath));
            foreach (var pair in _installedAlgorithms) _document.AlgorithmVersion[pair.Key] = pair.Value;
            Invalidate("模板已在当前上游预处理域教学并写入正式工程资源。");
        }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    public async Task CloseAsync()
    {
        BeginRequest(); await _operationGate.WaitAsync();
        try { await ResetExecutionServicesAsync(); Invalidate("会话已关闭。", false); ReleaseSource(); }
        finally { SetBusy(false, false); _operationGate.Release(); }
    }
    public ProjectDocument SnapshotProject() => _document with
    {
        ProjectName = ProjectName,
        Nodes = Nodes.Select(n => n.Specification with { Parameters = n.Parameters.Clone() }).ToList(),
        Connections = Connections.Select(c => c.ToContract()).ToList(), ROI = _document.ROI.ToList(),
        TemplateResources = _document.TemplateResources.ToList(), AlgorithmVersion = new(_document.AlgorithmVersion, StringComparer.Ordinal)
    };

    private void ReplaceGraph(ProjectDocument project, EditorLayout? layout)
    {
        _selectedNode = null; Notify(nameof(SelectedNode)); Connections.Clear(); Nodes.Clear();
        for (var i = 0; i < project.Nodes.Count; i++)
        {
            var spec = project.Nodes[i]; var location = layout?.Nodes.FirstOrDefault(n => n.NodeId == spec.NodeId);
            Nodes.Add(new(spec, Registry.Create(spec.NodeTypeId).Descriptor, location?.X ?? i % 4 * 330, location?.Y ?? i / 4 * 190));
        }
        foreach (var edge in project.Connections)
        {
            var source = Nodes.Single(n => n.NodeId == edge.SourceNodeId).AllOutputs.Single(p => p.Name == edge.SourcePort);
            var target = Nodes.Single(n => n.NodeId == edge.TargetNodeId).AllInputs.Single(p => p.Name == edge.TargetPort);
            Connections.Add(new(source, target));
        }
        SelectedNode = Nodes.FirstOrDefault();
    }
    private sealed record SessionExecution(RunResult Result, IReadOnlyList<RunResult> SupportingRuns) : IDisposable
    {
        public void Dispose() { Result.Dispose(); foreach (var support in SupportingRuns) support.Dispose(); }
    }
    private static NodeGraph DependencyGraph(ProjectDocument project, string target)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal); var queue = new Stack<string>(); queue.Push(target);
        while (queue.TryPop(out var node))
        {
            if (!selected.Add(node)) continue;
            foreach (var edge in project.Connections.Where(e => e.TargetNodeId == node)) queue.Push(edge.SourceNodeId);
        }
        return new(project.Nodes.Where(n => selected.Contains(n.NodeId)), project.Connections.Where(e => selected.Contains(e.SourceNodeId) && selected.Contains(e.TargetNodeId)));
    }
    private void Publish(SessionExecution execution)
    {
        var result = execution.Result;
        // Adopt the actual runtime input as the fallback preview. This removes the duplicate independently
        // decoded source after a run and prevents a changed ImageInput from reverting to an old image.
        var inputIds = Nodes.Where(n => n.TypeId is "ImageInput" or "VirtualCamera" or "CameraInput").Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
        var runtimeInput = execution.SupportingRuns.Append(result).SelectMany(r => r.Nodes.Values)
            .Where(n => inputIds.Contains(n.NodeId)).SelectMany(n => n.Outputs.Values).OfType<ImageFrame>().FirstOrDefault();
        if (runtimeInput is not null)
        {
            var next = runtimeInput.Share(); var previous = _sourceImage; _sourceImage = next; previous?.Dispose();
        }
        var allValues = execution.SupportingRuns.Append(result).SelectMany(r => r.Nodes.Values).SelectMany(n => n.Outputs.Values).ToArray();
        var frames = allValues.OfType<ImageFrame>().Concat(allValues.OfType<IterationTraceStore>().SelectMany(t => t.Traces).SelectMany(t => t.Nodes).SelectMany(n => n.Outputs.Values).OfType<ImageFrame>()).ToArray();
        var bytes = frames.Select(f => f.BufferHandle).Distinct().Sum(h => h.Length);
        var keepImages = bytes <= ResultImageBudgetBytes;
        foreach (var (id, node) in result.Nodes)
        {
            var model = Nodes.FirstOrDefault(n => n.NodeId == id);
            if (model is not null) { model.Status = model.IsEnabled ? node.Status.ToString() : "已禁用"; model.ErrorMessage = node.ErrorMessage ?? ""; model.ExecutionMilliseconds = node.ExecutionTime.TotalMilliseconds; model.Summary = NodeSummary(node.Outputs); }
            Results.Add(new(id, model?.Name ?? id, node.Status.ToString(), node.ExecutionTime.TotalMilliseconds, node.ErrorMessage ?? ""));
            _outputs[id] = node.Outputs.Where(v => keepImages || v.Value is not IDisposable).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        }
        var succeeded = result.Success; var error = result.ErrorMessage; var total = result.ExecutionTime.TotalMilliseconds;
        if (keepImages) { _retainedRun = result; _supportingRuns.AddRange(execution.SupportingRuns); } else execution.Dispose();
        BuildOverlays(); RefreshSelectedOutputs(); Notify(nameof(RetainedImageBytes)); Notify(nameof(CurrentOutputs));
        SetStatus(keepImages ? $"运行{(succeeded ? "完成" : "失败")} · {total:F2} ms" + (error is null ? "" : " · " + error)
            : "运行完成；图像结果超过缓存上限，仅保留数值输出。请使用 ROI 或减少中间图像。");
    }
    private void RefreshSelectedOutputs()
    {
        _selectedOutputInspection = null; Notify(nameof(SelectedOutputInspection));
        SelectedOutputs.Clear();
        if (SelectedNode is { } node && _outputs.TryGetValue(node.NodeId, out var outputs))
            foreach (var (port, value) in outputs) SelectedOutputs.Add(new(node.NodeId, port, Summarize(value), value));
        Notify(nameof(CurrentInspectionReport)); RefreshCollectionDisplay(); Notify(nameof(IterationTraces));
        var image = SelectedNode is { } selected ? FindDisplayImage(selected.NodeId, new()) : null;
        if (_pendingRoiBinding && _editedRoi is { } pending && image is not null && image.CoordinateSpace.Id != pending.CoordinateSpaceId &&
            _sourceImage?.CoordinateSpace.Id == pending.CoordinateSpaceId) image = _sourceImage;
        SetDisplay(image ?? _sourceImage);
        RestoreEditableRoi(); BuildOverlays();
    }
    private ImageFrame? FindDisplayImage(string id, HashSet<string> visited)
    {
        if (!visited.Add(id)) return null;
        if (_outputs.TryGetValue(id, out var values) && values.Values.OfType<ImageFrame>().FirstOrDefault() is { } image) return image;
        foreach (var edge in Connections.Where(c => c.Target.Node.NodeId == id))
            if (FindDisplayImage(edge.Source.Node.NodeId, visited) is { } parent) return parent;
        return null;
    }
    private void BuildOverlays()
    {
        var space = _displayImage?.CoordinateSpace.Id ?? _sourceImage?.CoordinateSpace.Id ?? "image";
        var failure = _outputs.Values.SelectMany(v => v.Values).OfType<TemplateMatchResult>().Any(m => !m.Success);
        _outputs.TryGetValue(SelectedNode?.NodeId ?? "", out var selectedValues);
        var explicitDisplay = SelectedNode?.TypeId == "ImageDisplay";
        var overlays = explicitDisplay ? new List<OverlayItem>() : _document.ROI.Where(r => r.CoordinateSpaceId == space)
            .Select(r => new OverlayItem("参考 ROI", r, OverlayKind.ReferenceRoi, !failure)).ToList();
        var values = explicitDisplay ? selectedValues?.Values ?? [] : _outputs.Values.SelectMany(v => v.Values);
        foreach (var value in values)
                switch (value)
                {
                    case TemplateMatchResult { Success: true, MatchedRegion: { } region } match when region.CoordinateSpaceId == space:
                        overlays.Add(new($"匹配 {match.Score:F3}", region, OverlayKind.Match)); break;
                    case RotatedRectRoi followed when !failure && followed.CoordinateSpaceId == space:
                        overlays.Add(new("跟随 ROI", followed, OverlayKind.FollowedRoi)); break;
                }
        var blobResults = selectedValues?.Values.OfType<BlobResults>().FirstOrDefault()
            ?? (explicitDisplay ? null : _outputs.Values.SelectMany(v => v.Values).OfType<BlobResults>().LastOrDefault(b => b.CoordinateSpaceId == space));
        if (blobResults is not null && blobResults.CoordinateSpaceId == space)
            overlays.AddRange(blobResults.Items.Select((blob, i) => new OverlayItem($"Blob {i + 1}", blob.Bounds.ToRotated(space), OverlayKind.Blob)));
        _overlays = overlays; Notify(nameof(Overlays));
        _measurementOverlays = _outputs.Values.SelectMany(v => v.Values).OfType<MeasurementResult>()
            .Where(r => r.Valid && r.CoordinateSpaceId == space).SelectMany(r => r.Overlays)
            .Where(o => o.CoordinateSpaceId == space).Distinct().Take(256).ToArray();
        Notify(nameof(MeasurementOverlays));
        _recognitionOverlays = values.SelectMany(value => value switch
        {
            OcrResult { Success: true } r => r.Overlays,
            CodeReadResult { Success: true } r => r.Overlays,
            YoloInferenceResult { Success: true } r => r.Overlays,
            _ => (IReadOnlyList<RecognitionOverlay>)[]
        }).Where(o => o.CoordinateSpaceId == space).Distinct().Take(256).ToArray();
        Notify(nameof(RecognitionOverlays));
    }
    private static string NodeSummary(IReadOnlyDictionary<string, object> values)
    {
        if (values.Values.OfType<IterationBatch>().FirstOrDefault() is { } batch)
            return $"已完成 {batch.CompletedCount}/{batch.Results.Count} · 异常 {batch.FailedCount}\n" + (batch.Cancelled ? "已取消 · 部分结果" : batch.Complete ? "遍历结束" : "未完成 · 请查看错误及未运行项");
        var display = values.Values.OfType<CollectionDisplayResult>().FirstOrDefault();
        if (display is not null) return display.Summary;
        var ocr = values.Values.OfType<OcrResult>().FirstOrDefault();
        if (ocr is not null) return $"识别：{ocr.Status} · {ocr.Confidence:P1}\n" + (ocr.Success ? BoundText(ocr.Text, 160) : BoundText(ocr.FailureReason ?? "未识别", 160));
        if (values.TryGetValue("Text", out var text) && text is string t) return t.Length == 0 ? "（空文本）" : BoundText(t, 240);
        var collection = values.Values.OfType<VisionCollection>().FirstOrDefault();
        return collection is null ? "" : $"{collection.Kind} · {collection.Count}项";
    }
    private static string BoundText(string text, int maximum) => text.Length <= maximum ? text : text[..maximum] + "…（详情查看完整内容）";
    private static string Summarize(object value) => value switch
    {
        VisionCollection c => $"{c.Kind} · {c.Count}项（只读集合）",
        VisionItem i => $"{i.Kind} · {i.State} · {i.Origin.Key}",
        CollectionDisplayResult d => d.Summary,
        IterationBatch b => $"已完成{b.CompletedCount}/{b.Results.Count} · 完整={b.Complete} · 取消={b.Cancelled}" + b.Error,
        IterationTraceStore t => $"{t.Traces.Count}个调试迭代 · 图像{t.RetainedBytes:N0} B",
        CameraFrameInfo frame => $"采集 #{frame.Sequence} · {frame.Width}×{frame.Height} {frame.PixelFormat} · {frame.SourceName}",
        ImageFrame image => $"{image.Width}×{image.Height} {image.PixelFormat}",
        TemplateMatchResult m => $"{(m.Success ? "成功" : "失配")} 分数={m.Score:F5} 中心=({m.CenterX:F3},{m.CenterY:F3}) 角度={m.AngleDegrees:F3}° 尺度={m.Scale:F4}" + (m.FailureReason is null ? "" : " " + m.FailureReason),
        InspectionDecision d => (d.IsOk ? "OK" : "NG") + " " + d.Reason,
        InspectionReport r => $"{(r.IsOk ? "OK" : "NG")} · Blob={r.BlobCount?.ToString() ?? "—"} · Score={r.MatchScore?.ToString("F3", CultureInfo.InvariantCulture) ?? "—"}" + (r.Communication is null ? "" : " · " + r.Communication.State),
        CommunicationStatus c => $"{c.State} · {c.BytesTransferred} B · {c.Description}",
        MeasurementResult m => $"{m.Kind} · {(m.Valid ? "有效" : "无效")} · {m.Unit}" + (m.ErrorReason is null ? "" : " · " + m.ErrorReason),
        OcrResult r => $"OCR · {(r.Success ? "成功" : "未识别")} · {r.Confidence:P1} · {r.Words.Count}词" + (r.FailureReason is null ? "" : " · " + r.FailureReason),
        CodeReadResult r => $"{r.Format} · {(r.Success ? "成功" : "未识别")} · {r.Codes.Count}个" + (r.FailureReason is null ? "" : " · " + r.FailureReason),
        YoloInferenceResult r => $"YOLO · {(r.Success ? "成功" : "失败")} · {r.Detections.Count}个目标 · {r.ExecutionTime.TotalMilliseconds:F1} ms" + (r.FailureReason is null ? "" : " · " + r.FailureReason),
        EdgePointSet p => $"{p.Points.Count} 个测量点 · {p.CoordinateSpaceId}",
        BlobResults b => $"{b.Count} 个对象；" + string.Join("; ", b.Items.Take(5).Select(i => $"面积={i.Area}, 中心=({i.CenterX:F2},{i.CenterY:F2})")) + (b.Count > 5 ? " …（点击查看字段）" : ""),
        CoordinateTransform2D t => $"{t.SourceSpaceId} → {t.TargetSpaceId}（仿射坐标变换）",
        Array array => $"{array.GetType().GetElementType()?.Name}[{array.Length}]",
        string text => text.Length <= 512 ? text : text[..512] + $" …（共{text.Length}字符）",
        bool boolean => boolean ? "True" : "False",
        _ => JsonSerializer.Serialize(value, value.GetType(), JsonDefaults.Options)
    };
    private void SetDisplay(ImageFrame? frame)
    {
        if (frame is not null && _displayImage is not null && !frame.IsDisposed && !_displayImage.IsDisposed &&
            frame.BufferHandle == _displayImage.BufferHandle && frame.Width == _displayImage.Width && frame.Height == _displayImage.Height && frame.CoordinateSpace == _displayImage.CoordinateSpace) return;
        var next = frame?.Share(); var old = _displayImage; _displayImage = next; Notify(nameof(DisplayImage)); old?.Dispose();
        if (frame is not null && _editedRoi is not null && frame.CoordinateSpace.Id != _editedRoi.CoordinateSpaceId) SetEditedRoi(null);
    }
    private void ClearResults()
    {
        _selectedOutputInspection = null; Notify(nameof(SelectedOutputInspection));
        SelectedOutputs.Clear(); Results.Clear(); _outputs.Clear(); SetDisplay(_sourceImage); RefreshCollectionDisplay(); Notify(nameof(IterationTraces));
        Notify(nameof(CurrentInspectionReport)); Notify(nameof(CurrentOutputs));
        var previous = _retainedRun; _retainedRun = null; previous?.Dispose();
        foreach (var support in _supportingRuns) support.Dispose(); _supportingRuns.Clear();
        foreach (var node in Nodes) { node.Status = node.IsEnabled ? "未运行" : "已禁用"; node.ErrorMessage = ""; node.ExecutionMilliseconds = 0; node.Summary = ""; }
        RestoreEditableRoi(); BuildOverlays(); Notify(nameof(RetainedImageBytes));
    }
    private void Invalidate(string message, bool markChanges = true, bool preserveRoiDraft = false)
    {
        Cancel(); _revision++; Notify(nameof(Revision));
        if (!preserveRoiDraft) { _pendingRoiBinding = false; _bindingPoses.Clear(); _bindingImageSpaces.Clear(); }
        ClearResults(); if (markChanges) SetChanges(true); SetStatus(message);
    }
    private void ReleaseSource()
    { SetDisplay(null); var old = _sourceImage; _sourceImage = null; old?.Dispose(); Notify(nameof(RetainedImageBytes)); }
    private void RestoreEditableRoi()
    {
        if (_pendingRoiBinding) return;
        var roi = SelectedNode?.TypeId switch
        {
            "ROITransform" => SelectedNode.Parameters.Deserialize<ROITransformParameters>(JsonDefaults.Options)?.ReferenceROI,
            "CropImage" or "RotatedCrop" => CropInputSpace(SelectedNode) is not null ? CropParameterRoi(SelectedNode) : null,
            "OCR" or "BarcodeReader" or "QRCodeReader" or "YoloInference" => RecognitionParameterRoi(SelectedNode) ?? _document.ROI.FirstOrDefault(),
            _ => _document.ROI.FirstOrDefault()
        };
        SetEditedRoi(roi is not null && (_displayImage is null || roi.CoordinateSpaceId == _displayImage.CoordinateSpace.Id) ? roi : null);
    }
    private RotatedRectRoi? RecognitionParameterRoi(EditorNode node)
    {
        var p = node.Parameters;
        if (!p.TryGetProperty("UseRoi", out var use) || use.ValueKind != JsonValueKind.True || CropInputSpace(node) is not { } space) return null;
        if (!p.TryGetProperty("RoiX", out var x) || !p.TryGetProperty("RoiY", out var y) || !p.TryGetProperty("RoiWidth", out var w) || !p.TryGetProperty("RoiHeight", out var h)) return null;
        return new RectRoi(x.GetDouble(), y.GetDouble(), w.GetDouble(), h.GetDouble()).ToRotated(space);
    }
    private RotatedRectRoi CropParameterRoi(EditorNode node)
    {
        var space = CropInputSpace(node) ?? _sourceImage?.CoordinateSpace.Id ?? "image";
        if (node.TypeId == "CropImage")
        {
            var p = node.Parameters.Deserialize<CropImageOptions>(JsonDefaults.Options)!;
            return new RectRoi(p.X, p.Y, p.Width, p.Height).ToRotated(space);
        }
        var rotated = node.Parameters.Deserialize<RotatedCropOptions>(JsonDefaults.Options)!;
        return new(rotated.CenterX + .5, rotated.CenterY + .5, rotated.Width, rotated.Height, rotated.Angle, space);
    }
    private string? CropInputSpace(EditorNode node)
    {
        var edge = Connections.FirstOrDefault(e => e.Target.Node == node && e.Target.Name == "Image");
        if (edge is null) return _sourceImage?.CoordinateSpace.Id;
        if (_outputs.TryGetValue(edge.Source.Node.NodeId, out var outputs) && outputs.TryGetValue(edge.Source.Name, out var value) && value is ImageFrame image)
            return image.CoordinateSpace.Id;
        if (_bindingImageSpaces.TryGetValue((edge.Source.Node.NodeId, edge.Source.Name), out var space)) return space;
        // A new crop creates a different local space each execution. Never guess it from the root image.
        var visited = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<EditorNode>(); pending.Push(edge.Source.Node);
        while (pending.TryPop(out var parent))
        {
            if (!visited.Add(parent.NodeId)) continue;
            if (parent.TypeId is "CropImage" or "RotatedCrop") return null;
            foreach (var connection in Connections.Where(c => c.Target.Node == parent && c.Target.Name == "Image")) pending.Push(connection.Source.Node);
        }
        return _sourceImage?.CoordinateSpace.Id;
    }
    private void RequireCropInputSpace(EditorNode node, RotatedRectRoi roi)
    {
        var space = CropInputSpace(node);
        if (space is null || space != roi.CoordinateSpaceId)
            throw new InvalidOperationException("ROI 与裁切输入图像坐标空间不一致；请在原图中绘制，或通过已有 ROITransform/CoordinateTransform 显式传递坐标。");
    }
    private static int IntegerRoi(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue || Math.Abs(value - Math.Round(value)) > 1e-8)
            throw new ArgumentException("裁切 ROI 的宽高及普通裁切边界必须为整数像素；请先调整 ROI 尺寸。");
        return checked((int)Math.Round(value));
    }
    private void SetFormalRoi(RotatedRectRoi roi)
    { if (_document.ROI.Count == 0) _document.ROI.Add(roi); else _document.ROI[0] = roi; }
    private void SetEditedRoi(RotatedRectRoi? roi) { _editedRoi = roi; Notify(nameof(EditedRoi)); }
    private void SetChanges(bool changed) { if (Set(ref _hasChanges, changed, nameof(HasChanges))) { } }
    private void SetBusy(bool value, bool blocking)
    { _blocking = blocking; Set(ref _busy, value, nameof(IsBusy)); }
    private void NotifyProjectProperties()
    { Notify(nameof(ProjectName)); Notify(nameof(ProjectPath)); Notify(nameof(ResourceRoot)); Notify(nameof(RetainedImageBytes)); }
    private long BeginRequest() { CheckAlive(); Cancel(); InvalidateAcquisition(); return ++_requests; }
    private void InvalidateAcquisition() { _acquisitionRevision++; Notify(nameof(AcquisitionRevision)); }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void CheckEditable() { CheckAlive(); if (_blocking) throw new InvalidOperationException("正在保存或切换工程，请等待当前操作完成。"); }
    private static ProjectDocument Rebase(ProjectDocument document, string oldRoot, string newRoot)
    {
        string Map(string path) => string.IsNullOrWhiteSpace(path) ? path : Path.GetRelativePath(newRoot, ProjectStore.ResolveResourcePath(oldRoot, path));
        return document with
        {
            Nodes = document.Nodes.Select(node => node.NodeTypeId switch
            {
                "ImageInput" => node with { Parameters = JsonDefaults.Element(node.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)! with
                    { Path = Map(node.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!.Path) }) },
                "VirtualCamera" => node with { Parameters = JsonDefaults.Element(node.Parameters.Deserialize<VirtualCameraParameters>(JsonDefaults.Options)! with
                    { FolderPath = Map(node.Parameters.Deserialize<VirtualCameraParameters>(JsonDefaults.Options)!.FolderPath) }) },
                "TemplateMatching" => node with { Parameters = JsonDefaults.Element(node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)! with
                    { TemplatePath = Map(node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!.TemplatePath) }) },
                _ => node
            }).ToList(),
            TemplateResources = document.TemplateResources.Select(r => r with { Path = Map(r.Path) }).ToList()
        };
    }
    private static void ValidateFileReferences(ProjectDocument document, string root)
    {
        foreach (var node in document.Nodes)
        {
            if (!node.IsEnabled) continue;
            if (node.NodeTypeId == "VirtualCamera")
            {
                var folder = node.Parameters.Deserialize<VirtualCameraParameters>(JsonDefaults.Options)!.FolderPath;
                if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(ProjectStore.ResolveResourcePath(root, folder)))
                    throw new DirectoryNotFoundException($"节点 {node.Name} 的相机图像文件夹不存在：{folder}");
            }
            var path = node.NodeTypeId == "ImageInput" ? node.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!.Path
                : node.NodeTypeId == "TemplateMatching" ? node.Parameters.Deserialize<TemplateMatchingParameters>(JsonDefaults.Options)!.TemplatePath : null;
            if (path is null) continue;
            var supplied = document.Connections.Any(e => e.TargetNodeId == node.NodeId && e.TargetPort == (node.NodeTypeId == "ImageInput" ? "Image" : "Template"));
            if (!supplied && (string.IsNullOrWhiteSpace(path) || !File.Exists(ProjectStore.ResolveResourcePath(root, path))))
                throw new FileNotFoundException($"节点 {node.Name} 引用的图像或模板资源不存在。", path);
        }
    }
    public void Dispose()
    {
        if (_disposed) return; InvalidateAcquisition(); _disposed = true; _requests++; Cancel();
        try { ClearResults(); ReleaseSource(); }
        finally { _executionSession.Dispose(); }
    }

    private async Task ResetExecutionServicesAsync()
    {
        var previous = _executionSession;
        await previous.DisposeAsync();
        _executionSession = new();
        _executor = new(Registry, logger: null, services: _executionSession);
    }
}

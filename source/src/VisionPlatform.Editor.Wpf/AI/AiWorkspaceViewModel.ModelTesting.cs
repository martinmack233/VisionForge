using System.Globalization;
using System.IO;
using VisionPlatform.Editor.Wpf.AI;

namespace VisionPlatform.Editor.Wpf.AI;
public sealed partial class AiWorkspaceViewModel
{
    private string _testModelPath = "", _testBackend = "onnx", _testDevice = "cpu", _testInputSize = "640", _testBatch = "1", _testContractMessage = "选择模型后校验实际输入；不会自动检测、导出或更换设备。";
    private bool _testBatchEditable = true, _testSizeEditable = true;
    private AiModelItem? _testModel;
    private string _inspectedTestModel = "";
    private long _modelSelectionRevision;
    public event Action? TestPageRequested;
    public AiModelItem? SelectedTestModel { get => _testModel; set { if (Set(ref _testModel, value) && value is not null) _ = RunUiAsync(() => SelectTestModelAsync(value)); } }
    public string TestModelPath { get => _testModelPath; set { if (Set(ref _testModelPath, value)) { _modelSelectionRevision++; _inspectedTestModel = ""; TestBatchEditable = TestSizeEditable = true; TestContractMessage = "模型已改变，请校验实际输入；未执行检测。"; Notify(nameof(TestModelDescription)); } } }
    public string TestBackend { get => _testBackend; set { if (Set(ref _testBackend, value)) Notify(nameof(TestModelDescription)); } }
    public string TestDevice { get => _testDevice; set { if (Set(ref _testDevice, value)) Notify(nameof(TestModelDescription)); } }
    public string TestInputSizeText { get => _testInputSize; set => Set(ref _testInputSize, value); }
    public string TestBatchText { get => _testBatch; set => Set(ref _testBatch, value); }
    public bool TestBatchEditable { get => _testBatchEditable; private set => Set(ref _testBatchEditable, value); }
    public bool TestSizeEditable { get => _testSizeEditable; private set => Set(ref _testSizeEditable, value); }
    public string TestContractMessage { get => _testContractMessage; private set => Set(ref _testContractMessage, value); }
    public string TestModelDescription => $"项目：{SelectedProject?.Name ?? "未选择"} · 类型：{Path.GetExtension(TestModelPath)} · 后端：{TestBackend} · 设备：{TestDevice}\n来源：{Models.FirstOrDefault(m => string.Equals(m.Path, TestModelPath, StringComparison.OrdinalIgnoreCase))?.RunId ?? "手动选择"}\n{(File.Exists(TestModelPath) ? "文件存在" : "文件不存在，请重新选择")} · {TestModelPath}";
    public string ModelBrowseDirectory
    {
        get
        {
            foreach (var path in new[] { TestModelPath, SelectedModel?.Path, Path.Combine(YoloRoot, "models", "pretrained", "placeholder.pt") })
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) return dir;
            return Path.IsPathFullyQualified(YoloRoot) && Directory.Exists(Path.Combine(YoloRoot, "models")) ? Path.Combine(YoloRoot, "models") : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
    public async Task UseSelectedModelForTestAsync()
    {
        if (SelectedModel is null) throw new ArgumentException("请先选择一个模型。");
        await SelectTestModelAsync(SelectedModel); TestPageRequested?.Invoke();
    }
    public async Task SelectTestModelAsync(AiModelItem model)
    {
        if (!File.Exists(model.Path)) throw new FileNotFoundException("该模型记录的实际文件已删除或移走，请重新选择。", model.Path);
        _testModel = model; Notify(nameof(SelectedTestModel)); TestModelPath = Path.GetFullPath(model.Path);
        TestBackend = Path.GetExtension(model.Path).Equals(".onnx", StringComparison.OrdinalIgnoreCase) ? "onnx" : "pt";
        // Device is always the user's explicit choice; do not switch to GPU/CPU here.
        await InspectTestModelAsync();
    }
    public async Task InspectTestModelAsync()
    {
        var path = (TestModelPath ?? "").Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') path = path[1..^1].Trim();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择模型的完整绝对路径，或点击右侧选择按钮。");
        if (!File.Exists(path)) throw new FileNotFoundException("所选模型文件不存在，请重新选择。", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".onnx" or ".pt")) throw new ArgumentException("请选择已有 .pt 或带 deployment.json 的 .onnx 检测模型。");
        if ((extension == ".onnx" ? "onnx" : "pt") != TestBackend) throw new ArgumentException("模型文件类型与所选推理后端不匹配，请显式选择对应后端。");
        TestModelPath = path; var revision = _modelSelectionRevision;
        if (extension == ".pt")
        {
            TestBatchEditable = TestSizeEditable = true; _inspectedTestModel = path;
            TestContractMessage = "PT：输入参数由原PyTorch引擎最终校验。现有PT后端使用CUDA设备编号，不支持cpu；本操作未加载权重或占用GPU。"; return;
        }
        var info = await Call("models.input_contract", new { model_path = path, project_id = SelectedProject?.Id,
            image_size = AiJson.Integer(TestInputSizeText, "测试输入尺寸", 32, 4096), batch = AiJson.Integer(TestBatchText, "测试批大小", 1, 64) });
        if (revision != _modelSelectionRevision || path != TestModelPath) return;
        var batch = AiJson.Optional(info, "fixed_batch"); var size = AiJson.Optional(info, "fixed_size");
        if (batch is { } b) TestBatchText = b.ToString("0", CultureInfo.InvariantCulture);
        if (size is { } s) TestInputSizeText = s.ToString("0", CultureInfo.InvariantCulture);
        TestBatchEditable = batch is null; TestSizeEditable = size is null; _inspectedTestModel = path;
        TestContractMessage = $"实际ONNX输入：{AiJson.Field(info, "shape")} · {(batch is null ? "动态Batch" : "固定Batch=" + TestBatchText)} · {(size is null ? "动态尺寸" : "固定尺寸=" + TestInputSizeText)}\n已校验模型哈希、deployment.json及原DetectorBackend（仅CPU检查输入，未检测）；当前测试设备仍为 {TestDevice}。";
        Notify(nameof(TestModelDescription));
    }
    private async Task ValidateTestBeforeRequestAsync()
    {
        RequireProject();
        // Always revalidate current file/hash. A historical completed status does not establish availability.
        await InspectTestModelAsync();
        if (TestBackend == "pt" && (!int.TryParse(TestDevice, out var gpu) || gpu < 0)) throw new ArgumentException("现有PT推理后端需要明确CUDA设备编号（如0）；不会静默改为GPU或CPU。ONNX可显式选择cpu。");
        if (TestBackend == "onnx" && TestDevice != "cpu" && (!int.TryParse(TestDevice, out var device) || device < 0)) throw new ArgumentException("ONNX设备须为cpu或明确GPU编号，是否可用由原引擎最终验证。");
    }
}

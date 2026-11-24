using System.IO;
using System.Text.RegularExpressions;

namespace VisionPlatform.Editor.Wpf.AI;

public sealed partial class AiWorkspaceViewModel
{
    private string _trainingDataset = "", _trainingModel = "", _device = "0", _epochs = "100", _batch = "8", _inputSize = "640", _learningRate = "0.01", _optimizer = "auto";
    private string _trainingModelError = "", _trainingDatasetError = "", _trainingSettingsError = "", _trainingContextError = "";
    public string TrainingModelError { get => _trainingModelError; private set => Set(ref _trainingModelError, value); }
    public string TrainingDatasetError { get => _trainingDatasetError; private set => Set(ref _trainingDatasetError, value); }
    public string TrainingSettingsError { get => _trainingSettingsError; private set => Set(ref _trainingSettingsError, value); }
    public string TrainingContextError { get => _trainingContextError; private set => Set(ref _trainingContextError, value); }
    public event Action<string>? TrainingValidationFailed;

    public static string CleanPathInput(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\''))) text = text[1..^1].Trim();
        return text;
    }
    private void ClearTrainingError(string field)
    {
        var previous = field switch { "Model" => TrainingModelError, "Dataset" => TrainingDatasetError, _ => TrainingSettingsError };
        if (field == "Model") TrainingModelError = ""; else if (field == "Dataset") TrainingDatasetError = ""; else TrainingSettingsError = "";
        if (Error == previous) Error = "";
    }
    private void FailTraining(string field, string message)
    {
        if (field == "Model") TrainingModelError = message;
        else if (field == "Dataset") TrainingDatasetError = message;
        else if (field == "Context") TrainingContextError = message;
        else TrainingSettingsError = message;
        TrainingValidationFailed?.Invoke(field);
        throw new ArgumentException(message);
    }
    public async Task StartTrainingAsync(bool resume)
    {
        TrainingContextError = "";
        if (!IsConnected) FailTraining("Context", "请先配置已有 Python 环境并连接 AI 工作区。");
        if (SelectedProject is null) FailTraining("Context", "请先选择 AI 项目。");
        object payload;
        if (resume)
        {
            // The original task owns its dataset, parameters and valid checkpoint. New-run drafts
            // must not replace those values or prevent a legitimate checkpoint resume.
            var taskId = SelectedTask?.Kind == "training" ? SelectedTask.Id : _trainingTaskId;
            if (string.IsNullOrWhiteSpace(taskId)) FailTraining("Context", "请先选择有有效检查点的原训练任务。");
            payload = new { project_id = RequireProject(), task_id = taskId };
        }
        else
        {
            TrainingModelError = TrainingDatasetError = TrainingSettingsError = "";
            TrainingModel = CleanPathInput(TrainingModel); TrainingDataset = CleanPathInput(TrainingDataset);
            if (TrainingModel.Length == 0) FailTraining("Model", "请先选择初始权重。");
            if (!Path.IsPathFullyQualified(TrainingModel)) FailTraining("Model", "请选择完整路径，或点击右侧“选择”按钮。");
            if (!File.Exists(TrainingModel)) FailTraining("Model", "所选初始权重不存在，请重新选择。");
            if (!Path.GetExtension(TrainingModel).Equals(".pt", StringComparison.OrdinalIgnoreCase))
                FailTraining("Model", "训练初始权重需要 .pt 文件，ONNX 用于推理或部署。");
            if (TrainingDataset.Length == 0) FailTraining("Dataset", "请先选择已生成的数据集 data.yaml。");
            if (!Path.IsPathFullyQualified(TrainingDataset)) FailTraining("Dataset", "请选择数据集 data.yaml 的完整路径，或点击“选择数据集”。");
            if (!File.Exists(TrainingDataset)) FailTraining("Dataset", "所选数据集 data.yaml 不存在，请重新生成或选择。");
            if (Path.GetExtension(TrainingDataset).ToLowerInvariant() is not (".yaml" or ".yml")) FailTraining("Dataset", "训练数据集需要已生成的 data.yaml 文件。");
            try
            {
                var epochs = AiJson.Integer(EpochsText, "轮数", 1, 100000); var batch = AiJson.Integer(BatchText, "批大小", 1, 4096);
                var size = AiJson.Integer(InputSizeText, "输入尺寸", 32, 8192); var rate = AiJson.Parse(LearningRateText, "学习率", double.Epsilon, 1);
                if (size % 32 != 0) throw new ArgumentException("输入尺寸须为 32 的倍数，避免框架自动更改尺寸。");
                if (!Optimizers.Contains(Optimizer)) throw new ArgumentException("请选择支持的优化器。");
                if (!Regex.IsMatch(Device.Trim(), @"^(?:cuda:)?\d+$", RegexOptions.IgnoreCase)) throw new ArgumentException("训练需要单张 CUDA GPU 编号，例如 0 或 cuda:0；不会自动切换 CPU。");
                payload = new { project_id = RequireProject(), config = new { dataset = TrainingDataset, model = TrainingModel, device = Device.Trim(), epochs, batch, imgsz = size, lr0 = rate, optimizer = Optimizer, workers = 0, amp = UseAmp, allow_download = false } };
            }
            catch (ArgumentException ex) { FailTraining("Settings", ex.Message); throw; }
        }
        System.Text.Json.JsonElement response;
        try { response = await Call(resume ? "training.resume" : "training.start", payload); }
        catch (VisionPlatform.AI.AiServiceException ex)
        {
            var field = resume ? "Context" : ex.Code switch { "model_missing" or "model_type" or "absolute_path_required" => "Model", "dataset_missing" => "Dataset", _ => "Settings" };
            if (field == "Model") TrainingModelError = ex.Message; else if (field == "Dataset") TrainingDatasetError = ex.Message; else if (field == "Context") TrainingContextError = ex.Message; else TrainingSettingsError = ex.Message;
            TrainingValidationFailed?.Invoke(field); throw;
        }
        _trainingTaskId = AiJson.Text(response, "task_id", "id"); _logOffset = 0; LogText = ""; Metrics.Clear(); RememberTask(response, "training");
    }
}

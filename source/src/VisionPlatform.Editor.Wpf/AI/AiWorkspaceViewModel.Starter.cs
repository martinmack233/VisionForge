using System.Globalization;
using System.IO;
using System.Text.Json;

namespace VisionPlatform.Editor.Wpf.AI;

public sealed partial class AiWorkspaceViewModel
{
    private Task _projectChangeTask = Task.CompletedTask;
    private string _starterStatus = "离线可查看说明和12张合成原图；执行准备需先配置现有Python并连接工作区。不会下载模型或自动训练。";
    private string _preparationStatus = "预览仅查看切片；切片任务完成后仍需生成可训练数据集。进度数量不是训练Epoch。";
    private string _preparedDatasetPath = "";
    private string? _preparationTaskId, _preparationProjectId, _preparedProjectId;
    public event Action? TrainingPageRequested;
    public string StarterResourceDirectory => Path.Combine(AppContext.BaseDirectory, "AIExamples", "starter-v1");
    public string StarterPreviewPath => Path.Combine(StarterResourceDirectory, "source-01.png");
    public string StarterInstructions => File.Exists(Path.Combine(StarterResourceDirectory, "README.md"))
        ? File.ReadAllText(Path.Combine(StarterResourceDirectory, "README.md")) : "示例说明未找到，请重新解压完整发布包。";
    public string StarterStatus { get => _starterStatus; private set => Set(ref _starterStatus, value); }
    public string DataPreparationStatus { get => _preparationStatus; private set => Set(ref _preparationStatus, value); }
    public string PreparedDatasetPath { get => _preparedDatasetPath; private set { if (Set(ref _preparedDatasetPath, value)) Notify(nameof(CanUsePreparedDataset)); } }
    public bool CanUsePreparedDataset => HasProject && _preparedProjectId == SelectedProject?.Id && File.Exists(PreparedDatasetPath);
    public async Task PrepareStarterExampleAsync(bool fresh)
    {
        if (!IsConnected) { StarterStatus = "请先在环境设置中选择已有Python和数据目录，再连接工作区。离线说明和静态图片仍可查看。"; return; }
        await Work(async () =>
        {
            StarterStatus = "正在通过原导入服务准备独立示例图片、类别和标注；不会启动训练。";
            try
            {
                var response = await Call("examples.starter.prepare", new { fresh });
                var projectId = AiJson.Text(AiJson.Field(response, "project"), "id");
                await RefreshProjectsAsync(); SelectedProject = Projects.Single(p => p.Id == projectId); await _projectChangeTask;
                var options = AiJson.Field(response, "recommended");
                TileWidthText = AiJson.Number(options, "tile_width").ToString(CultureInfo.InvariantCulture); TileHeightText = AiJson.Number(options, "tile_height").ToString(CultureInfo.InvariantCulture);
                OverlapText = AiJson.Number(options, "overlap").ToString(CultureInfo.InvariantCulture); Grouping = "batch"; TrainRatioText = (2d / 3).ToString("R", CultureInfo.InvariantCulture); ValidationRatioText = TestRatioText = (1d / 6).ToString("R", CultureInfo.InvariantCulture); DatasetTiled = true;
                foreach (var property in new[] { nameof(TileWidthText), nameof(TileHeightText), nameof(OverlapText), nameof(Grouping), nameof(TrainRatioText), nameof(ValidationRatioText), nameof(TestRatioText), nameof(DatasetTiled) }) Notify(property);
                SelectedImage = Images.FirstOrDefault(); StarterStatus = AiJson.Text(response, "message");
            }
            catch (Exception ex) { StarterStatus = "示例准备未完成：" + ex.GetBaseException().Message; throw; }
        });
    }
    public void UsePreparedDatasetForTraining()
    {
        if (!CanUsePreparedDataset) throw new InvalidOperationException("请先为当前AI项目成功生成可训练数据集 data.yaml。");
        TrainingDataset = PreparedDatasetPath; TrainingPageRequested?.Invoke();
        Status = "已将本次成功的数据集带入训练页。请检查初始权重和CUDA配置，再显式开始训练；未替换权重或启动任务。";
    }
    private void ResetPreparationContext()
    {
        _preparationTaskId = _preparationProjectId = _preparedProjectId = null; PreparedDatasetPath = "";
        DataPreparationStatus = "预览、切片任务、可训练数据集是三个步骤；这些步骤均不会启动训练。";
        Notify(nameof(CanUsePreparedDataset));
    }
    private void UpdatePreparationStatus(JsonElement[] tasks)
    {
        if (_preparationProjectId != SelectedProject?.Id || _preparationTaskId is null) return;
        var task = tasks.FirstOrDefault(t => AiJson.Text(t, "task_id") == _preparationTaskId);
        if (task.ValueKind != JsonValueKind.Object) return;
        var kind = AiJson.Text(task, "kind"); var state = AiJson.Text(task, "status");
        if (state == "completed")
        {
            if (kind == "dataset")
            {
                var resultPath = AiJson.Text(AiJson.Field(task, "result"), "path");
                var yaml = Path.IsPathFullyQualified(resultPath) ? Path.Combine(resultPath, "data.yaml") : "";
                _preparedProjectId = _preparationProjectId; PreparedDatasetPath = File.Exists(yaml) ? yaml : "";
                DataPreparationStatus = CanUsePreparedDataset ? "可训练数据集生成完成。点击“用于模型训练”进入下一步；训练尚未开始。" : "数据处理返回完成，但未找到data.yaml，请查看任务详情。";
            }
            else DataPreparationStatus = "切片任务完成；尚未生成可训练数据集，也没有开始训练。请点击“生成可训练数据集”。";
        }
        else if (state is "failed" or "cancelled" or "interrupted") DataPreparationStatus = "数据准备" + state + "：" + AiJson.Text(task, "error") + "。没有启动训练。";
        else
        {
            var progress = AiJson.Field(task, "progress");
            DataPreparationStatus = $"{(kind == "dataset" ? "数据集生成" : "切片处理")} · {AiJson.Number(progress, "current")} / {AiJson.Number(progress, "total")} 张/片 · 非训练Epoch。";
        }
    }
}

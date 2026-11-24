using System.Windows;
using System.Windows.Controls;
using VisionPlatform.AI.Deployment;
using VisionPlatform.Editor.Wpf.AI;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Wpf;

public partial class MainWindow
{
    private async Task ApplyAiModelAsync(AiModelSelection selection, bool createNew)
    {
        if (Session.IsBusy) throw new InvalidOperationException("请先停止当前检测流程，再配置 AI 模型。");
        if (ParametersView.HasUnappliedDraft)
        {
            var decision = ChooseAiDialog("处理参数草稿", "当前节点有尚未应用的参数。请选择先应用或明确放弃，再应用 AI 模型。", "先应用草稿", "放弃草稿", "取消");
            if (decision == 0) return;
            if (decision == 1 && !ParametersView.ApplyChanges()) throw new InvalidOperationException("参数草稿未通过校验，模型尚未应用。");
            if (decision == 2) ParametersView.DiscardDraft();
        }
        var verified = await AiModelDeployment.VerifyAsync(_aiClient, selection.ModelPath, selection.ImageSize, selection.AiProjectId);
        var machine = new VisionPlatform.Contracts.Deployment.MachineConfigurationStore().Load();
        if (!System.IO.File.Exists(machine.DetectionPython) || !System.IO.File.Exists(System.IO.Path.Combine(machine.DetectionComponentRoot, "component.json")))
            throw new InvalidOperationException("请先在“首次配置与部署诊断”中配置独立AI检测组件；训练工作区路径不再写入新检测节点。");
        using var edit = Session.BeginEdit("将 AI 模型应用到检测工程");
        AiModelDeployment.ApplyToNode(Session, verified, _aiClient.Settings, new AiNodeApplyOptions
        {
            ProjectId = selection.AiProjectId, WorkspaceId = selection.AiWorkspaceId, ModelRecordId = selection.ModelRecordId ?? "",
            Confidence = selection.Confidence, Iou = selection.Iou, MergeIou = selection.MergeIou,
            ClassIds = selection.ClassIds,
            Tiled = selection.Tiled, TileWidth = selection.TileWidth, TileHeight = selection.TileHeight, Overlap = selection.Overlap
        }, createNew, portableEnvironment: true);
        if (selection.AiProjectId.Length > 0 && selection.AiWorkspaceId.Length > 0)
            Session.SetAiProjectLink(new AiProjectLink(selection.AiProjectId, selection.AiWorkspaceId, _aiClient.Settings.DataHome));
        FitEditorViews();
    }
    private Task<AiCloseChoice> ConfirmAiCloseAsync(string message)
    {
        var decision = ChooseAiDialog("AI 任务仍在运行", message + "\n保留训练时，任务 ID 和输出目录会保存，下次连接后可继续管理；其他短时任务将取消。", "停止任务后关闭", "保留训练并关闭", "返回工作区");
        return Task.FromResult(decision switch { 1 => AiCloseChoice.StopTasks, 2 => AiCloseChoice.ContinueTraining, _ => AiCloseChoice.Cancel });
    }
    private int ChooseAiDialog(string title, string message, string first, string second, string cancel)
    {
        var choice = 0;
        var dialog = new Window { Title = title, Owner = this, Width = 580, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        dialog.SetResourceReference(BackgroundProperty, "StudioPanel"); dialog.SetResourceReference(ForegroundProperty, "StudioForeground");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (text, value) in new[] { (first, 1), (second, 2), (cancel, 0) })
        {
            var button = new Button { Content = text, IsCancel = value == 0, MinWidth = 90 };
            button.Click += (_, _) => { choice = value; dialog.DialogResult = value != 0; };
            actions.Children.Add(button);
        }
        panel.Children.Add(actions); dialog.Content = panel; dialog.ShowDialog(); return choice;
    }
}

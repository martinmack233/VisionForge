using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Core.Session;

public sealed partial class EditorSession
{
    public void SetAiProjectLink(AiProjectLink? link)
    {
        CheckEditable();
        if (link is not null && (string.IsNullOrWhiteSpace(link.ProjectId) || string.IsNullOrWhiteSpace(link.WorkspaceId) || !Path.IsPathFullyQualified(link.DataHome)))
            throw new ArgumentException("请选择具有独立标识和数据目录的 AI 项目。");
        if (_document.AiProject == link) return;
        using var edit = BeginEdit("关联 AI 项目");
        _document = _document with { SchemaVersion = ProjectDocument.CurrentSchemaVersion, AiProject = link };
        Invalidate(link is null ? "已解除 AI 项目关联；数据集、模型和训练历史均保留。" : "AI 项目已关联；数据存储独立，未执行训练或检测。");
    }
}

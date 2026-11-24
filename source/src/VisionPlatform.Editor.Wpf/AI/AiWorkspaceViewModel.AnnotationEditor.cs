namespace VisionPlatform.Editor.Wpf.AI;

public sealed partial class AiWorkspaceViewModel
{
    private string _annotationEditError = "";
    private AiClassItem? _annotationClass;
    public AiAnnotationDraft AnnotationDraft { get; } = new();
    public string AnnotationEditError { get => _annotationEditError; private set => Set(ref _annotationEditError, value); }
    public AiClassItem? AnnotationClass { get => _annotationClass; set { if (Set(ref _annotationClass, value)) AnnotationEditError = ""; } }
    public bool HasAnnotationImage => SelectedImage is not null && SelectedImage.Id == _loadedImage?.Id && ImageWidth > 0 && ImageHeight > 0;
    public bool HasSelectedAnnotation => HasAnnotationImage && SelectedBox is not null && Boxes.Contains(SelectedBox);
    public string AnnotationImageDescription => HasAnnotationImage ? $"{SelectedImage!.Name}\n{ImageWidth} × {ImageHeight} 像素 · {Boxes.Count} 个标注" : "尚未选择图片。请在数据集页导入并选择图片。";
    public string AnnotationSelectionDescription => HasSelectedAnnotation ? "编辑后点击“应用坐标 / 类别”；未编辑字段保留原始精度。" : "尚未选择标注。请在图像或标注列表中选择，或绘制新标注。";
    private void InitializeAnnotationEditor()
    {
        AnnotationDraft.Changed += () => AnnotationEditError = "";
        Boxes.CollectionChanged += (_, _) => RefreshAnnotationLabels();
    }
    private void RefreshAnnotationLabels()
    {
        for (var i = 0; i < Boxes.Count; i++) Boxes[i].DisplayName = $"{i + 1}. {Classes.FirstOrDefault(c => c.Id == Boxes[i].ClassId)?.Name ?? "未知类别"}";
        NotifyAnnotationAvailability();
    }
    private void NotifyAnnotationAvailability()
    {
        foreach (var property in new[] { nameof(HasAnnotationImage), nameof(HasSelectedAnnotation), nameof(AnnotationImageDescription), nameof(AnnotationSelectionDescription) }) Notify(property);
    }
    private void ReloadAnnotationDraft()
    {
        AnnotationDraft.Load(SelectedBox); AnnotationClass = Classes.FirstOrDefault(c => c.Id == SelectedBox?.ClassId); AnnotationEditError = ""; NotifyAnnotationAvailability();
    }
    public void ApplyAnnotationCoordinates()
    {
        if (!HasSelectedAnnotation || SelectedBox is not { } original) { AnnotationEditError = "请先选择图片和标注。"; return; }
        try
        {
            var candidate = AnnotationDraft.Prepare(original);
            if (AnnotationClass is null || !Classes.Any(c => c.Id == AnnotationClass.Id)) throw new ArgumentException("请选择当前项目中的有效类别。");
            candidate.ClassId = AnnotationClass.Id;
            if (candidate.Width <= 0 || candidate.Height <= 0) throw new ArgumentException("宽度和高度必须大于0。");
            if (candidate.X + candidate.Width > ImageWidth || candidate.Y + candidate.Height > ImageHeight) throw new ArgumentException("标注超出原图边界，请检查X、Y、宽度和高度。");
            if (candidate.X != original.X || candidate.Y != original.Y || candidate.Width != original.Width || candidate.Height != original.Height || candidate.ClassId != original.ClassId)
            {
                BeginAnnotationEdit(); original.X = candidate.X; original.Y = candidate.Y; original.Width = candidate.Width; original.Height = candidate.Height; original.ClassId = candidate.ClassId;
                AnnotationDraft.Load(original); RefreshAnnotationLabels(); AnnotationEdited();
            }
            else AnnotationDraft.Load(original);
            AnnotationEditError = "";
        }
        catch (ArgumentException ex) { AnnotationEditError = ex.Message; }
    }
}

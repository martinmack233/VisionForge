using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using VisionPlatform.Editor.Core.Common;

namespace VisionPlatform.Editor.Wpf.AI;

public enum AiCloseChoice { Cancel, StopTasks, ContinueTraining }
public sealed record AiProjectSelection(string ProjectId, string WorkspaceId, string DataHome, string Name);
public sealed record AiModelSelection(string ModelPath, JsonElement Deployment, double Confidence, double Iou,
    int ImageSize, bool Tiled, int TileWidth, int TileHeight, double Overlap, double MergeIou,
    string AiProjectId, string AiWorkspaceId, string? ModelRecordId)
{ public string ClassIds { get; init; } = ""; }
public sealed record AiProjectItem(string Id, string Name);
public sealed record AiClassItem(int Id, string Name, string Color, int AnnotationCount = 0, string Shortcut = "") { public string Label => $"{Id} · {Name}（{AnnotationCount} 框） {Shortcut}"; }
public sealed record AiImageItem(string Id, string Name, string Path, string ThumbnailPath, int Width, int Height,
    bool Reviewed, string Batch, int AnnotationCount)
{
    public string State => Reviewed ? AnnotationCount == 0 ? "已检查 / 正常" : "已检查 / 有标注" : "未检查";
    public string Description => $"{Width} × {Height} · {State}";
}
public sealed record AiModelItem(string Id, string Name, string Path, string RunId, string Created,
    string Metrics, string Sha256)
{ public bool Exists => System.IO.File.Exists(Path); public string Availability => Exists ? "文件存在" : "文件缺失"; public string Label => $"{Name} · {Kind} · {Availability}"; public string Kind => System.IO.Path.GetExtension(Path).Equals(".onnx", StringComparison.OrdinalIgnoreCase) ? "ONNX" : "训练权重"; }
public sealed record AiTaskItem(string Id, string Kind, string Status, int Current, int Total, string Message, string Error)
{
    public bool Active => Status is "running" or "waiting" or "starting" or "stopping";
    public double Percent => Total > 0 ? 100d * Current / Total : 0;
    public string ProgressText
    {
        get
        {
            var stage = Kind switch { "slices" => "切片处理", "dataset" => "数据集生成", "training" => "训练Epoch", "import" => "图像导入", "inference" => "模型检测", _ => Kind };
            return stage + " · " + (Total > 0 ? $"{Current} / {Total}" : "等待实际进度") + (Kind is "slices" or "dataset" ? "（不是训练进度）" : "");
        }
    }
}
public sealed record AiMetricPoint(double Epoch, double? BoxLoss, double? ClassLoss, double? Precision, double? Recall, double? Map50, double? Map);
public sealed record AiDetectionItem(int ClassId, string ClassName, double Confidence, double X, double Y, double Width, double Height);
public sealed record AiInferenceImageItem(string Id, string Name, string SourcePath, string PreviewPath, int Width, int Height,
    string Quality, double ElapsedMilliseconds, string ModelHash, string FrameId, string SourceKind, string TaskId, JsonElement Boxes);

public sealed class AiBox : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private int _classId;
    private double _x, _y, _width, _height;
    private string _displayName = "";
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Label { get; set; } = "";
    public string Id { get => _id; set => Set(ref _id, value); }
    public int ClassId { get => _classId; set => Set(ref _classId, value); }
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public AiBox Copy() => new() { Id = Id, ClassId = ClassId, X = X, Y = Y, Width = Width, Height = Height, Label = Label };
    public object ToPayload() => new { id = Id, class_id = ClassId, x = X, y = Y, w = Width, h = Height };
}

internal static class AiJson
{
    public static JsonElement Field(JsonElement value, params string[] names)
    { foreach (var name in names) if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)) return field; return default; }
    public static string Text(JsonElement value, params string[] names)
    {
        var field = names.Length == 0 ? value : Field(value, names);
        return field.ValueKind switch { JsonValueKind.String => field.GetString() ?? "", JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => field.ToString(), _ => "" };
    }
    public static double Number(JsonElement value, params string[] names)
    { var field = names.Length == 0 ? value : Field(value, names); return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0; }
    public static double? Optional(JsonElement value, params string[] names)
    { var field = Field(value, names); return field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : null; }
    public static bool Boolean(JsonElement value, params string[] names)
    { var field = Field(value, names); return field.ValueKind == JsonValueKind.True || field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var i) && i != 0; }
    public static JsonElement[] Items(JsonElement value, params string[] names)
    {
        var field = names.Length == 0 ? value : Field(value, names);
        return field.ValueKind == JsonValueKind.Array ? field.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
    }
    public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    public static double Parse(string value, string label, double minimum, double maximum)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) && result >= minimum && result <= maximum
            ? result : throw new ArgumentException($"{label}须为 {minimum:G}～{maximum:G} 的有效数值。");
    public static int Integer(string value, string label, int minimum, int maximum)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= minimum && result <= maximum
            ? result : throw new ArgumentException($"{label}须为 {minimum}～{maximum} 的整数。");
}

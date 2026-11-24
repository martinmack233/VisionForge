using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Common;

namespace VisionPlatform.Editor.Core.Parameters;

public enum ParameterKind { Integer, Number, Boolean, String, Enum }
public sealed record ParameterFieldDescriptor(string Path, string Label, string Description, Type ValueType,
    ParameterKind Kind, bool Nullable, string DefaultText, double? Minimum, double? Maximum, IReadOnlyList<string> Choices,
    bool DefaultIsNull = false)
{public IReadOnlyList<ParameterCondition> Conditions{get;init;}=[];}
public sealed record ParameterCondition(string Path,IReadOnlyList<string> Values);
public sealed record ParameterSectionDescriptor(string Path, string Label, bool Optional);
public sealed record ParameterMetadata(IReadOnlyList<ParameterFieldDescriptor> Fields, IReadOnlyList<ParameterSectionDescriptor> Sections);

/// <summary>Editor-only reflection metadata. Algorithms can supply rules without building WPF parameter pages.</summary>
public static class ParameterMetadataService
{
    private static readonly IReadOnlyDictionary<string, (double? Minimum, double? Maximum)> Rules = new Dictionary<string, (double?, double?)>
    {
        ["KernelSize"]=(1,null), ["SigmaX"]=(0,null), ["SigmaY"]=(0,null),
        ["MinimumScore"]=(0,1), ["CoarseScale"]=(.1,1), ["ScaleMin"]=(.5,1.5), ["ScaleMax"]=(.5,1.5),
        ["ScaleStep"]=(.005,.25), ["AngleStepDegrees"]=(.5,720), ["FineAngleStepDegrees"]=(.1,720),
        ["AngleMinDegrees"]=(-360,360), ["AngleMaxDegrees"]=(-360,360),
        ["MinArea"]=(1,null), ["MaxArea"]=(1,null), ["MinimumBlobCount"]=(0,null), ["MaximumBlobCount"]=(0,null),
        ["Width"]=(double.Epsilon,null), ["Height"]=(double.Epsilon,null), ["Scale"]=(double.Epsilon,null)
    };
    private static readonly IReadOnlyDictionary<string,string> Labels = new Dictionary<string,string>
    {
        ["Path"]="图像路径", ["PixelFormat"]="输入像素格式", ["TargetFormat"]="目标像素格式", ["Options"]="算法参数",
        ["KernelSize"]="核大小（正奇数）", ["SigmaX"]="Sigma X", ["SigmaY"]="Sigma Y", ["BorderType"]="边界模式",
        ["TemplatePath"]="模板资源路径", ["AlgorithmId"]="匹配算法", ["MinimumScore"]="最低分数", ["SearchRoi"]="搜索区域",
        ["AngleMinDegrees"]="最小角度（°）", ["AngleMaxDegrees"]="最大角度（°）", ["AngleStepDegrees"]="粗角度步长（°）", ["FineAngleStepDegrees"]="细角度步长（°）",
        ["ScaleMin"]="最小尺度", ["ScaleMax"]="最大尺度", ["ScaleStep"]="尺度步长", ["CoarseScale"]="粗搜索图像比例",
        ["ROI"]="滤波区域", ["ReferenceROI"]="参考检测区域", ["ReferencePose"]="参考模板姿态", ["Threshold"]="分割阈值",
        ["MinArea"]="最小面积", ["MaxArea"]="最大面积", ["DarkForeground"]="暗色前景", ["MinimumBlobCount"]="最少对象数", ["MaximumBlobCount"]="最多对象数",
        ["CoordinateSpaceId"]="坐标空间", ["CenterX"]="中心 X", ["CenterY"]="中心 Y", ["Width"]="宽度", ["Height"]="高度", ["AngleDegrees"]="顺时针角度（°）", ["Scale"]="尺度",
        ["ThresholdValue"]="阈值", ["MaxValue"]="最大输出值", ["ThresholdType"]="阈值类型", ["Operation"]="操作类型",
        ["KernelWidth"]="核宽度", ["KernelHeight"]="核高度", ["KernelShape"]="核形状", ["Iterations"]="迭代次数",
        ["Area"]="面积筛选", ["AspectRatio"]="长宽比筛选", ["Circularity"]="圆度筛选",
        ["Enabled"]="启用条件", ["Minimum"]="最小值", ["Maximum"]="最大值", ["Sort"]="排序方式", ["MaximumCount"]="最大保留数量",
        ["CropRectangle"]="裁切矩形", ["Interpolation"]="插值方法", ["BorderMode"]="越界填充方式", ["BorderValue"]="边界填充值", ["Angle"]="顺时针角度（°）"
    };
    public static ParameterMetadata Describe(NodeAuthoringDefinition definition)
    {
        var fields = new List<ParameterFieldDescriptor>(); var sections = new List<ParameterSectionDescriptor>();
        Walk(definition.ParameterType, "", JsonNode.Parse(definition.DefaultParameters.GetRawText()), fields, sections, 0);
        return new(fields, sections);
    }
    private static void Walk(Type type, string prefix, JsonNode? defaults, List<ParameterFieldDescriptor> fields, List<ParameterSectionDescriptor> sections, int depth,IReadOnlyList<ParameterCondition>? inherited=null)
    {
        if (depth > 8) throw new ArgumentException("Parameter metadata nesting exceeds eight levels.");
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false))
        {
            var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            var conditions=(inherited??[]).Concat(property.GetCustomAttributes<AppliesWhenAttribute>().Select(a=>new ParameterCondition(prefix.Length==0?a.Path:prefix+"."+a.Path,a.Values))).ToArray();
            var actual = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null;
            var value = defaults is JsonObject obj ? obj[property.Name] : null;
            var label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? Labels.GetValueOrDefault(property.Name, property.Name);
            if (!TryKind(actual, out var kind))
            {
                sections.Add(new(path, label, value is null));
                Walk(actual, path, value, fields, sections, depth + 1,conditions); continue;
            }
            var range = property.GetCustomAttribute<RangeAttribute>();
            double? min = null, max = null;
            if (kind == ParameterKind.Integer)
            {
                if (actual == typeof(byte)) { min=byte.MinValue; max=byte.MaxValue; }
                else if (actual == typeof(short)) { min=short.MinValue; max=short.MaxValue; }
                else if (actual == typeof(int)) { min=int.MinValue; max=int.MaxValue; }
                else if (actual == typeof(long)) { min=long.MinValue; max=long.MaxValue; }
            }
            if (Rules.TryGetValue(property.Name, out var rule)) { min=rule.Minimum ?? min; max=rule.Maximum ?? max; }
            if (range is not null) { min=Convert.ToDouble(range.Minimum,CultureInfo.InvariantCulture); max=Convert.ToDouble(range.Maximum,CultureInfo.InvariantCulture); }
            var defaultValue = property.GetCustomAttribute<DefaultValueAttribute>()?.Value;
            var text = value is null ? DefaultText(actual,property.Name) : ValueText(value);
            if (defaultValue is not null) text=Convert.ToString(defaultValue,CultureInfo.InvariantCulture) ?? "";
            fields.Add(new(path,label,property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? path,actual,kind,nullable,text,min,max,
                actual.IsEnum ? Enum.GetNames(actual) : [], value is null && defaultValue is null){Conditions=conditions});
        }
    }
    internal static string ValueText(JsonNode value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
    private static string DefaultText(Type type,string name) => type == typeof(string) ? name == "CoordinateSpaceId" ? "image" : ""
        : type == typeof(bool) ? "false" : type.IsEnum ? Enum.GetNames(type)[0] : name is "Width" or "Height" ? "10" : name == "Scale" ? "1" : "0";
    private static bool TryKind(Type type,out ParameterKind kind)
    {
        kind=type.IsEnum ? ParameterKind.Enum : type==typeof(string) ? ParameterKind.String : type==typeof(bool) ? ParameterKind.Boolean
            : type==typeof(byte)||type==typeof(short)||type==typeof(int)||type==typeof(long) ? ParameterKind.Integer : ParameterKind.Number;
        return type.IsEnum || type==typeof(string) || type==typeof(bool) || type==typeof(byte)||type==typeof(short)||type==typeof(int)||type==typeof(long)
            || type==typeof(float)||type==typeof(double)||type==typeof(decimal);
    }
}

public sealed class ParameterValue : ObservableObject
{
    private string _text; private bool _useValue;
    public ParameterFieldDescriptor Descriptor { get; }
    public string Text { get=>_text; set { if(Set(ref _text,value)) Notify(nameof(BooleanValue)); } }
    public bool UseValue { get=>_useValue; set=>Set(ref _useValue,value); }
    public bool BooleanValue { get=>bool.TryParse(Text,out var result)&&result; set=>Text=value ? "true" : "false"; }
    public string RangeHint => Descriptor.Minimum is null && Descriptor.Maximum is null ? "" : $"范围 {Descriptor.Minimum?.ToString("G",CultureInfo.InvariantCulture) ?? "−∞"} … {Descriptor.Maximum?.ToString("G",CultureInfo.InvariantCulture) ?? "+∞"}";
    public ParameterValue(ParameterFieldDescriptor descriptor,JsonNode? current) { Descriptor=descriptor; _text=current is null ? descriptor.DefaultText : ParameterMetadataService.ValueText(current); _useValue=current is not null || !descriptor.Nullable; }
}
public sealed class ParameterSection : ObservableObject
{
    private bool _enabled;
    public ParameterSectionDescriptor Descriptor { get; }
    public bool Enabled { get=>_enabled; set=>Set(ref _enabled,value); }
    public ParameterSection(ParameterSectionDescriptor descriptor,bool enabled) {Descriptor=descriptor;_enabled=enabled;}
}

/// <summary>All edits live in an independent JSON draft. Build never changes a running NodeSpecification.</summary>
public sealed class ParameterDraft
{
    private readonly JsonObject _original;
    public IReadOnlyList<ParameterValue> Fields { get; }
    public IReadOnlyList<ParameterSection> Sections { get; }
    public ParameterDraft(NodeAuthoringDefinition definition,JsonElement current)
    {
        _original=JsonNode.Parse(current.GetRawText()) as JsonObject ?? throw new ArgumentException("Parameters must be an object.");
        var metadata=ParameterMetadataService.Describe(definition);
        Sections=metadata.Sections.Select(s=>new ParameterSection(s,Get(_original,s.Path) is not null || !s.Optional)).ToArray();
        Fields=metadata.Fields.Select(f=>new ParameterValue(f,Get(_original,f.Path))).ToArray();
    }
    public bool IsApplicable(ParameterValue value)=>value.Descriptor.Conditions.All(c=>c.Values.Contains(Fields.FirstOrDefault(f=>f.Descriptor.Path==c.Path)?.Text??"",StringComparer.OrdinalIgnoreCase));
    public void ResetDefaults()
    {
        foreach(var field in Fields) { field.Text=field.Descriptor.DefaultText; field.UseValue=!field.Descriptor.Nullable || !field.Descriptor.DefaultIsNull; }
        foreach(var section in Sections) section.Enabled=!section.Descriptor.Optional;
    }
    public bool TryBuild(out JsonElement json,out IReadOnlyList<string> errors)
    {
        var result=(JsonObject)_original.DeepClone(); var problems=new List<string>();
        foreach(var section in Sections.OrderBy(s=>s.Descriptor.Path.Count(c=>c=='.')))
        {
            if(HasDisabledParent(section.Descriptor.Path)) continue;
            if(!section.Enabled) Put(result,section.Descriptor.Path,null);
            else if(Get(result,section.Descriptor.Path) is not JsonObject) Put(result,section.Descriptor.Path,new JsonObject());
        }
        foreach(var value in Fields)
        {
            if(HasDisabledParent(value.Descriptor.Path)||!IsApplicable(value)) continue;
            if(!value.UseValue && value.Descriptor.Nullable) {Put(result,value.Descriptor.Path,null);continue;}
            try {Put(result,value.Descriptor.Path,Parse(value));}
            catch(Exception ex) when(ex is ArgumentException or FormatException or OverflowException) {problems.Add(value.Descriptor.Path+": "+ex.Message);}
        }
        errors=problems;
        json=problems.Count==0 ? JsonSerializer.SerializeToElement(result,JsonDefaults.Options) : default;
        return problems.Count==0;
    }
    private bool HasDisabledParent(string path)=>Sections.Any(s=>!s.Enabled && path.StartsWith(s.Descriptor.Path+".",StringComparison.Ordinal));
    private static JsonNode? Parse(ParameterValue value)
    {
        var d=value.Descriptor; var text=value.Text;
        if(d.Kind==ParameterKind.String) return JsonValue.Create(text);
        if(d.Kind==ParameterKind.Enum)
        { if(!d.Choices.Contains(text,StringComparer.Ordinal)) throw new ArgumentException("请选择有效枚举值。"); return JsonValue.Create(text); }
        if(d.Kind==ParameterKind.Boolean)
        { if(!bool.TryParse(text,out var flag)) throw new ArgumentException("需要布尔值。");return JsonValue.Create(flag); }
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number) || !double.IsFinite(number)) throw new ArgumentException("需要有限数值（小数点使用 .）。");
        if(d.Minimum is {} min && number<min || d.Maximum is {} max && number>max) throw new ArgumentOutOfRangeException(nameof(value),value.RangeHint);
        if(d.Kind==ParameterKind.Integer)
        {
            if(!long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var integer)) throw new ArgumentException("需要整数。");
            var converted=Convert.ChangeType(integer,d.ValueType,CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToNode(converted,d.ValueType,JsonDefaults.Options);
        }
        return JsonSerializer.SerializeToNode(Convert.ChangeType(number,d.ValueType,CultureInfo.InvariantCulture),d.ValueType,JsonDefaults.Options);
    }
    private static JsonNode? Get(JsonObject root,string path)
    {JsonNode? current=root;foreach(var item in path.Split('.')) {if(current is not JsonObject obj)return null;current=obj[item];}return current;}
    private static void Put(JsonObject root,string path,JsonNode? value)
    {var segments=path.Split('.');var current=root;foreach(var item in segments[..^1]) {if(current[item] is not JsonObject next) {next=new JsonObject();current[item]=next;}current=next;}current[segments[^1]]=value;}
}

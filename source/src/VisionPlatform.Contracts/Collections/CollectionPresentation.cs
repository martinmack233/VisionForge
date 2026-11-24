using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace VisionPlatform.Contracts.Collections;
public enum CollectionLabelPosition { FollowTarget, Above, Below, TopLeft, TopRight, BottomLeft, BottomRight }
public sealed record CollectionDisplayOptions
{
    [DisplayName("显示字段（逗号分隔；仅显示实际值）")] public string Fields { get; init; } = "Index,Class,Text,Confidence,Area,State";
    [DisplayName("小数位数"), Range(0, 12)] public int DecimalPlaces { get; init; } = 3;
    [DisplayName("只显示指定业务状态")] public bool FilterState { get; init; }
    [DisplayName("业务状态")] public BusinessState State { get; init; }
    [DisplayName("字号 DIP"), Range(6, 72)] public double FontSize { get; init; } = 14;
    [DisplayName("文字颜色 #RRGGBB")] public string TextColor { get; init; } = "#243B53";
    [DisplayName("文字背景 #RRGGBB")] public string BackgroundColor { get; init; } = "#FFFFFF";
    [DisplayName("文字背景透明度 0..1"), Range(0, 1)] public double BackgroundOpacity { get; init; } = .85;
    [DisplayName("图形线宽 DIP"), Range(.1, 12)] public double LineWidth { get; init; } = 2;
    [DisplayName("默认线色 #RRGGBB")] public string LineColor { get; init; } = "#2C7BE5";
    [DisplayName("填充颜色 #RRGGBB")] public string FillColor { get; init; } = "#B9D8FF";
    [DisplayName("填充透明度 0..1"), Range(0, 1)] public double FillOpacity { get; init; } = .12;
    [DisplayName("按业务状态着色")] public bool UseStateColors { get; init; } = true;
    [DisplayName("OK颜色")] public string OkColor { get; init; } = "#238B68";
    [DisplayName("NG颜色")] public string NgColor { get; init; } = "#D94A53";
    [DisplayName("未识别颜色")] public string UnrecognizedColor { get; init; } = "#D99A21";
    [DisplayName("执行错误颜色")] public string ErrorColor { get; init; } = "#A62CA6";
    [DisplayName("标签位置")] public CollectionLabelPosition Position { get; init; } = CollectionLabelPosition.Above;
    [DisplayName("显示偏移X DIP"), Range(-10000, 10000)] public double OffsetX { get; init; }
    [DisplayName("显示偏移Y DIP"), Range(-10000, 10000)] public double OffsetY { get; init; }
    [DisplayName("节点摘要行数"), Range(1, 8)] public int SummaryLines { get; init; } = 3;
    [DisplayName("最大叠加数"), Range(0, 2000)] public int MaximumOverlays { get; init; } = 100;
    public string Color(VisionItem item) => !UseStateColors ? LineColor : item.State switch
    { BusinessState.Ok => OkColor, BusinessState.Ng => NgColor, BusinessState.Unrecognized => UnrecognizedColor, BusinessState.Error => ErrorColor, _ => UnrecognizedColor };
}
public sealed record CollectionDisplayResult(VisionCollection Collection, CollectionDisplayOptions Options)
{
    public IEnumerable<(int Index, VisionItem Item)> VisibleItems => Collection.Items.Select((v, i) => (i, v)).Where(p => !Options.FilterState || p.v.State == Options.State);
    public string Label(VisionItem item, int index)
    {
        var values = item.Fields;
        return string.Join("  ", Options.Fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Equals("Index", StringComparison.OrdinalIgnoreCase) ? $"#{index}" : field.Equals("State", StringComparison.OrdinalIgnoreCase) ? item.State.ToString()
                : values.TryGetValue(field, out var v) ? field + ": " + v.Format(Options.DecimalPlaces) : "").Where(v => v.Length > 0));
    }
    public string Summary => Collection.Count == 0 ? "空集合（0项）" :
        $"{Collection.Kind} · {Collection.Count}项" + Environment.NewLine + string.Join(Environment.NewLine, VisibleItems.Take(Options.SummaryLines).Select(v => Bound(Label(v.Item, v.Index), 150)));
    public string CopyText() => string.Join(Environment.NewLine, VisibleItems.Select(v => $"{v.Index}\t{v.Item.Origin.Key}\t{Label(v.Item, v.Index)}"));
    private static string Bound(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

using System.Text.Json;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Core.Parameters;

public sealed record DisplayFieldChoice(string Key, string Label);
/// <summary>Presentation metadata only; field keys and runtime values remain unchanged.</summary>
public static class DisplayFieldCatalog
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"]="布尔值", ["Index"]="序号（从0开始）", ["State"]="业务判定", ["Text"]="文本", ["Class"]="类别名称", ["ClassId"]="类别编号", ["Confidence"]="置信度",
        ["Area"]="面积", ["Label"]="目标标签", ["Perimeter"]="周长", ["Circularity"]="圆度", ["X"]="中心X", ["Y"]="中心Y",
        ["Width"]="宽度", ["Height"]="高度", ["Angle"]="角度", ["Radius"]="半径", ["Status"]="执行状态", ["Error"]="错误或原因",
        ["Milliseconds"]="耗时（ms）", ["Number"]="数值结果", ["Distance"]="距离", ["Valid"]="测量有效", ["Unit"]="单位",
        ["Length"]="线段长度", ["EndX"]="终点X", ["EndY"]="终点Y", ["SignedArea"]="有向面积", ["AspectRatio"]="旋转矩形宽高比", ["Rectangularity"]="矩形度", ["IsHole"]="孔洞边界", ["ParentIndex"]="父轮廓索引", ["EllipseWidth"]="椭圆宽度", ["EllipseHeight"]="椭圆高度", ["EllipseAngle"]="椭圆角度", ["AnalysisDetail"]="形状分析说明",
        ["Kind"]="测量类型", ["PointCount"]="点数", ["Closed"]="闭合轮廓", ["Format"]="码格式", ["DirectionX"]="方向X", ["DirectionY"]="方向Y"
    };
    private static readonly string[] Geometry = ["X", "Y", "Width", "Height", "Angle"];
    public static string Label(string key) => Labels.GetValueOrDefault(key, key);
    public static IReadOnlyList<DisplayFieldChoice> Available(IEditorSession session, EditorNode display)
    {
        var connection = session.Connections.FirstOrDefault(e => e.Target.Node == display && e.Target.Name == "Collection");
        var keys = new List<string> { "Index", "State" };
        if (connection is not null)
        {
            var source = connection.Source;
            if (session.CurrentOutputs.TryGetValue(source.Node.NodeId, out var output) && output.GetValueOrDefault(source.Name) is VisionCollection collection)
                keys.AddRange(collection.Count == 0 ? Infer(session, source.Node, new HashSet<string>()) : collection.Items.SelectMany(i => i.Fields.Keys));
            else keys.AddRange(Infer(session, source.Node, new HashSet<string>()));
        }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(k => new DisplayFieldChoice(k, Label(k))).ToArray();
    }
    private static IEnumerable<string> Infer(IEditorSession session, EditorNode node, HashSet<string> visited)
    {
        if (!visited.Add(node.NodeId)) return [];
        if (node.TypeId is "CollectionSlice" or "CollectionSort" or "CollectionFilter" or "CollectionDisplay")
        {
            var previous = session.Connections.FirstOrDefault(e => e.Target.Node == node && e.Target.Name == "Collection");
            return previous is null ? [] : Infer(session, previous.Source.Node, visited);
        }
        if (node.TypeId=="RandomNumber"||node.TypeId=="ModbusDecode")return ["Number","Index"];
        if(node.TypeId=="ModbusCommunication")return node.Parameters.TryGetProperty("Operation",out var modbusOp)&&(modbusOp.GetString()?.Contains("Coil")==true||modbusOp.GetString()?.Contains("Discrete")==true)?["Boolean","Index"]:["Number","Index"];
        if (node.TypeId == "FindContours") return ["PointCount", "Closed", "ParentIndex", "IsHole"];
        if (node.TypeId == "ContourAnalysis") return Geometry.Concat(["Area", "SignedArea", "Perimeter", "Circularity", "AspectRatio", "Rectangularity", "EllipseWidth", "EllipseHeight", "EllipseAngle", "AnalysisDetail", "PointCount", "Closed", "IsHole"]);
        if (node.TypeId == "HoughLinesP") return ["X", "Y", "EndX", "EndY", "Length", "DirectionX", "DirectionY"];
        if (node.TypeId == "HoughCircles") return ["X", "Y", "Radius"];
        if (node.TypeId is "RoiCollection" or "CollectionToRois") return Geometry;
        if (node.TypeId == "CollectionAdapter")
        {
            var input = session.Connections.FirstOrDefault(e => e.Target.Node == node && e.Target.Name != "Image")?.Target.Name;
            return input switch
            {
                "Blobs" => Geometry.Concat(["Area", "Label"]), "Detections" => Geometry.Concat(["Class", "ClassId", "Confidence"]),
                "OCR" => Geometry.Concat(["Text", "Confidence"]), "Codes" => ["Text", "Format"], "ROI" => Geometry,
                "Points" => ["PointCount"], "Contour" => ["PointCount", "Closed"], "Line" => ["X", "Y", "DirectionX", "DirectionY"],
                "Circle" => ["X", "Y", "Radius"], _ => []
            };
        }
        if (node.TypeId == "ForEach")
        {
            try
            {
                var options = node.Parameters.Deserialize<ForEachOptions>(JsonDefaults.Options)!;
                var ports = options.Body.Connections.Where(e => e.TargetNodeId == options.Body.OutputNodeId).Select(e => e.TargetPort).ToHashSet();
                var fields = new List<string> { "Status", "Error", "Milliseconds" };
                if (ports.Contains("OCR") || ports.Contains("Text")) fields.Add("Text");
                if (ports.Contains("OCR") || ports.Contains("Confidence")) fields.Add("Confidence");
                if (ports.Contains("Number")) fields.Add("Number");
                if (ports.Contains("ROI")) fields.AddRange(Geometry);
                return fields;
            }
            catch (System.Text.Json.JsonException) { return []; }
        }
        return [];
    }
}

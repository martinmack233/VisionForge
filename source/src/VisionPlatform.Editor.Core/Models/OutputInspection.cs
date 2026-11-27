using VisionPlatform.Contracts.Collections;
using System.Globalization;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Editor.Core.Models;

public sealed record OutputDetailField(string Name, string Value);
/// <summary>Display metadata only. It never retains an ImageFrame or native lease.</summary>
public sealed record OutputInspection(string NodeId, string PortName, string DataType, string Summary,
    IReadOnlyList<OutputDetailField> Fields)
{
    public string? FullText { get; init; }
}

public static class OutputInspector
{
    private static string Text(object? value) => value switch
    {
        null => "—", bool b => b ? "True" : "False", string s when s.Length > 16384 => s[..16384] + $"\n…（共{s.Length}字符，仅显示前16384字符）", double d => d.ToString("G12", CultureInfo.InvariantCulture),
        float f => f.ToString("G9", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    public static string Category(object value) => value switch
    {
        VisionCollection or VisionItem or CollectionDisplayResult => "集合与项目",
        VisionPlatform.Contracts.Data.FileReadResult or VisionPlatform.Contracts.Data.TabularData or VisionPlatform.Contracts.Data.BinaryData=>"文件与表格",
        FloatPlaneFrame => "浮点图像（数值）", VisionPlatform.Contracts.Numerics.NumericMatrix => "数值矩阵", ProjectiveTransform2D => "透视坐标变换",
        ImageFrame => "图像", bool => "Boolean", string => "文本", InspectionReport or InspectionDecision => "检测结果",
        BlobResults => "目标集合", TemplateMatchResult or Pose2D => "定位", CoordinateTransform2D or RectRoi or RotatedRectRoi => "几何",
        MeasurementResult or MeasurementSummary or EdgePointSet or LineGeometry or CircleGeometry => "几何测量",
        OcrResult => "字符识别", CodeReadResult => "条码识别", YoloInferenceResult => "AI 检测",
        CameraFrameInfo => "图像采集",
        CommunicationStatus => "通信", ControlSignal => "控制", _ when NodePorts.IsNumeric(value.GetType()) => "数值", _ => "其他数据"
    };
    public static OutputInspection Describe(OutputValueRow row)
    {
        var fields = new List<OutputDetailField>();
        void Add(string name, object? value) => fields.Add(new(name, Text(value)));
        switch (row.Value)
        {
            case VisionPlatform.Communication.Modbus.ModbusResult modbus:
                Add("协议响应已验证",modbus.ResponseReceived);Add("重复写已抑制",modbus.DuplicateSuppressed);Add("UnitId",modbus.UnitId);Add("0基地址",modbus.Address);Add("功能操作",modbus.Operation);Add("事务ID",modbus.TransactionId);Add("耗时ms",modbus.Milliseconds);Add("线圈",string.Join(", ",modbus.Coils));Add("原始UInt16寄存器",string.Join(", ",modbus.Registers));break;
            case VisionPlatform.Communication.Modbus.ModbusBitValues bits:Add("强类型Boolean数量",bits.Count);Add("值",string.Join(", ",bits));break;
            case VisionPlatform.Communication.Modbus.ModbusRegisterValues words:Add("原始UInt16数量",words.Count);Add("值",string.Join(", ",words));Add("解码","使用Modbus寄存器解码节点，显式字节序和字序");break;
            case VisionPlatform.Communication.Modbus.ModbusConnectionReference reference:Add("会话",reference.SessionId);Add("连接",reference.Id);Add("代次",reference.Generation);Add("传输",reference.Transport);break;
            case VisionPlatform.Contracts.Data.FileReadResult file:
                Add("文件",file.Path);Add("本次内容哈希",file.Sha256);Add("格式",file.Format);Add("编码",file.Encoding);Add("实际字节",file.ByteCount);if(file.Table is{}table){Add("数据行",table.RowCount);Add("列",table.ColumnCount);Add("表头",string.Join(" | ",table.Headers));foreach(var csvRow in table.Rows.Take(100))Add("数据行",string.Join(" | ",csvRow.Select(v=>v.Length>512?v[..512]+"…":v)));}if(file.Text is{}fileText)Add("文本预览",fileText);if(file.Bytes is{}fileBytes)Add("二进制字节数",fileBytes.Count);break;
            case VisionPlatform.Contracts.Data.BinaryData binary:
                Add("字节数",binary.Count);Add("前256字节",Convert.ToHexString(Enumerable.Range(0,Math.Min(binary.Count,256)).Select(i=>binary[i]).ToArray()));break;
            case VisionPlatform.Contracts.Numerics.NumericMatrix matrix:
                Add("类型","Float64，行主序，只读");Add("行",matrix.Rows);Add("列",matrix.Columns);for(var y=0;y<Math.Min(matrix.Rows,16);y++)Add("行 "+y,string.Join("  ",Enumerable.Range(0,Math.Min(matrix.Columns,16)).Select(x=>matrix[y,x].ToString("G17",CultureInfo.InvariantCulture))));if(matrix.Rows>16||matrix.Columns>16)Add("预览范围","最多16×16；完整矩阵仍在输出中");break;
            case ProjectiveTransform2D transform:
                Add("方向",transform.SourceSpaceId+" → "+transform.TargetSpaceId);Add("矩阵",string.Join(", ",transform.Matrix.Values.Select(v=>v.ToString("G17",CultureInfo.InvariantCulture))));Add("意义","正向像素中心映射；透视矩阵不视为仿射");break;
            case FloatPlaneFrame plane:
                Add("数据类型","Float32真实带符号数值，非Mono8");Add("尺寸",$"{plane.Width}×{plane.Height}");Add("量",plane.Quantity);Add("单位",plane.Unit);Add("帧",plane.FrameId);Add("坐标",plane.CoordinateSpace);
                using(var lease=plane.Lease()){var samples=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(lease.GetRowSpan(0));for(var x=0;x<Math.Min(32,samples.Length);x++)Add($"首行[{x}]",samples[x]);}
                Add("预览",plane.Quantity == "neighborhood-sum" ? "实际邻域和；需要图像预览时选择均值方式。" : "查看Preview口，8位绝对值预览不能代替原梯度");break;
            case EnhancementStatistics statistics:
                Add("实际统计规则",statistics.Rule);Add("帧",statistics.FrameId);Add("尺寸",$"{statistics.Width}×{statistics.Height}");foreach(var channel in statistics.Channels)Add("通道 "+channel.Channel,$"输入[{channel.InputMinimum:G17},{channel.InputMaximum:G17}] 均值{channel.InputMean:G17} / 输出[{channel.OutputMinimum:G17},{channel.OutputMaximum:G17}] 均值{channel.OutputMean:G17}");break;
            case VisionCollection collection:
                Add("类型", collection.Kind); Add("数量", collection.Count); Add("合法字段", string.Join(", ", VisionPlatform.Plugins.Builtin.CollectionFields.Supported(collection))); Add("查看", "集合/逐项面板显示完整列表与来源"); break;
            case VisionItem item:
                Add("来源关联", item.Origin.Key); Add("FrameId", item.Origin.FrameId); Add("坐标空间", item.Origin.CoordinateSpaceId); foreach (var p in item.Fields) Add(p.Key, p.Value.Format(12)); break;
            case CollectionDisplayResult display:
                Add("数量", display.Collection.Count); Add("摘要", display.Summary); Add("显示配置", System.Text.Json.JsonSerializer.Serialize(display.Options, JsonDefaults.Options)); break;

            case CameraFrameInfo captured:
                Add("帧ID", captured.FrameId); Add("采集序号", captured.Sequence); Add("时间戳", captured.Timestamp); Add("宽度", captured.Width); Add("高度", captured.Height);
                Add("Stride", captured.Stride); Add("像素格式", captured.PixelFormat); Add("源文件", captured.SourceName); break;
            case RegionStatistics statistics:
                Add("统计有效", statistics.Valid); Add("均值", statistics.Mean); Add("总体标准差", statistics.StandardDeviation); Add("选中像素数", statistics.PixelCount);
                Add("非零像素数", statistics.NonZeroCount); Add("前景比例", statistics.ForegroundRatio); Add("帧ID", statistics.FrameId); Add("原因", statistics.Error); break;
            case ColorPlaneFrame channels:
                Add("工作颜色空间", channels.ColorSpace); Add("宽度", channels.Width); Add("高度", channels.Height); Add("Stride", channels.Stride);
                Add("帧ID", channels.FrameId); Add("坐标空间", channels.CoordinateSpace.Id); Add("预览", "查看显式转换后的Preview端口，工作通道不能按BGR直接显示。"); break;
            case ImageFrame image:
                Add("宽度", image.Width); Add("高度", image.Height); Add("像素格式", image.PixelFormat); Add("Stride", image.Stride);
                Add("坐标空间", image.CoordinateSpace.Id); Add("原点 X", image.CoordinateSpace.OriginX); Add("原点 Y", image.CoordinateSpace.OriginY); break;
            case TemplateMatchResult match:
                Add("匹配成功", match.Success); Add("分数", match.Score); Add("中心 X", match.Success ? match.CenterX : null); Add("中心 Y", match.Success ? match.CenterY : null);
                Add("角度", match.Success ? match.AngleDegrees : null); Add("尺度", match.Success ? match.Scale : null); Add("耗时 ms", match.ExecutionTime.TotalMilliseconds); Add("原因", match.FailureReason); break;
            case Pose2D pose:
                Add("中心 X", pose.CenterX); Add("中心 Y", pose.CenterY); Add("角度", pose.AngleDegrees); Add("尺度", pose.Scale); Add("坐标空间", pose.CoordinateSpaceId); break;
            case CoordinateTransform2D map:
                Add("映射", map.SourceSpaceId + " → " + map.TargetSpaceId); Add("M11", map.M11); Add("M12", map.M12); Add("M21", map.M21); Add("M22", map.M22); Add("OffsetX", map.OffsetX); Add("OffsetY", map.OffsetY); break;
            case BlobResults blobs:
                Add("对象数量", blobs.Count); Add("坐标空间", blobs.CoordinateSpaceId);
                foreach (var blob in blobs.Items.Take(100)) Add("Blob " + blob.Label, $"面积 {blob.Area}; 中心 ({Text(blob.CenterX)}, {Text(blob.CenterY)}); 宽×高 {Text(blob.Bounds.Width)}×{Text(blob.Bounds.Height)}");
                if (blobs.Count > 100) Add("显示限制", "只列前100个对象；完整结果仍在当前运行输出中。"); break;
            case InspectionReport report:
                Add("结果", report.IsOk ? "OK" : "NG"); Add("目标数量", report.BlobCount); Add("分数", report.MatchScore); Add("中心 X", report.CenterX); Add("中心 Y", report.CenterY);
                Add("角度", report.Angle); Add("尺度", report.Scale); Add("算法耗时 ms", report.ExecutionMilliseconds); Add("通信", report.Communication?.Description);
                if (report.Measurement is { } reportMeasurement) DescribeMeasurement(reportMeasurement); break;
            case MeasurementResult measuredResult:
                Add("坐标空间", measuredResult.CoordinateSpaceId); DescribeMeasurement(measuredResult.Summary); break;
            case MeasurementSummary measuredSummary: DescribeMeasurement(measuredSummary); break;
            case EdgePointSet points:
                Add("坐标空间", points.CoordinateSpaceId); Add("点数", points.Points.Count);
                foreach (var (point, index) in points.Points.Take(100).Select((p, i) => (p, i))) Add("点 " + index, $"({Text(point.X)}, {Text(point.Y)})");
                if (points.Points.Count > 100) Add("显示限制", "仅显示前100个点；完整结果不变。"); break;
            case LineGeometry line:
                Add("坐标空间", line.CoordinateSpaceId); Add("原点 X", line.Origin.X); Add("原点 Y", line.Origin.Y);
                Add("方向 X", line.Direction.X); Add("方向 Y", line.Direction.Y); break;
            case CircleGeometry circle:
                Add("坐标空间", circle.CoordinateSpaceId); Add("圆心 X", circle.Center.X); Add("圆心 Y", circle.Center.Y); Add("半径 px", circle.Radius); Add("直径 px", circle.Radius * 2); break;
            case OcrResult ocr:
                Add("成功", ocr.Success); Add("文本", ocr.Text); Add("置信度 (0..1)", ocr.Confidence); Add("词数", ocr.Words.Count);
                Add("坐标空间", ocr.CoordinateSpaceId); Add("耗时 ms", ocr.ExecutionTime.TotalMilliseconds); Add("原因", ocr.FailureReason);
                foreach (var word in ocr.Words.Take(100)) Add(word.Text, $"置信度 {Text(word.Confidence)}; ({Text(word.Bounds.X)}, {Text(word.Bounds.Y)}) {Text(word.Bounds.Width)}×{Text(word.Bounds.Height)}");
                if (ocr.Words.Count > 100) Add("显示限制", "仅显示前100词。"); break;
            case CodeReadResult code:
                Add("成功", code.Success); Add("码数", code.Codes.Count); Add("坐标空间", code.CoordinateSpaceId); Add("耗时 ms", code.ExecutionTime.TotalMilliseconds); Add("原因", code.FailureReason);
                foreach (var item in code.Codes.Take(100)) { Add("码制", item.Format); Add("文本", item.Text); Add("定位点（并非完整边框）", string.Join("; ", item.Points.Take(32).Select(p => $"({Text(p.X)}, {Text(p.Y)})"))); } break;
            case YoloInferenceResult yolo:
                Add("成功", yolo.Success); Add("目标数", yolo.Detections.Count); Add("模型 SHA256", yolo.ModelSha256); Add("执行提供者", yolo.Provider);
                Add("Tile 数", yolo.TileCount); Add("Forward ms", yolo.ForwardMilliseconds); Add("节点耗时 ms", yolo.ExecutionTime.TotalMilliseconds); Add("坐标空间", yolo.CoordinateSpaceId); Add("原因", yolo.FailureReason);
                foreach (var item in yolo.Detections.Take(100)) Add(item.Label, $"类别 {item.ClassId}; 分数 {Text(item.Confidence)}; ({Text(item.Bounds.X)}, {Text(item.Bounds.Y)}) {Text(item.Bounds.Width)}×{Text(item.Bounds.Height)}");
                if (yolo.Detections.Count > 100) Add("显示限制", "仅显示前100个目标。"); break;
            case InspectionDecision decision: Add("结果", decision.IsOk ? "OK" : "NG"); Add("原因", decision.Reason); break;
            case CommunicationStatus status: Add("状态", status.State); Add("成功", status.Success); Add("字节数", status.BytesTransferred); Add("说明", status.Description); Add("错误", status.ErrorCode); break;
            case RectRoi rect: Add("X", rect.X); Add("Y", rect.Y); Add("宽度", rect.Width); Add("高度", rect.Height); break;
            case RotatedRectRoi rect: Add("中心 X", rect.CenterX); Add("中心 Y", rect.CenterY); Add("宽度", rect.Width); Add("高度", rect.Height); Add("角度", rect.AngleDegrees); break;
            case ControlSignal signal: Add("本次活动", signal.IsActive); break;
            case Array array:
                Add("元素数量", array.Length);
                for (var i = 0; i < Math.Min(100, array.Length); i++) Add("[" + i + "]", array.GetValue(i)); break;
            default: Add("值", row.Value); break;
        }
        return new(row.NodeId, row.PortName, row.Value.GetType().Name, row.Summary, fields)
        { FullText = row.Value is string text ? text : row.Value is OcrResult fullOcr ? fullOcr.Text : row.Value is VisionPlatform.Contracts.Data.FileReadResult fileResult ? fileResult.Text : null };
        void DescribeMeasurement(MeasurementSummary measured)
        {
            Add("测量类型", measured.Kind); Add("有效", measured.Valid); Add("单位", measured.Unit); Add("错误原因", measured.ErrorReason);
            foreach (var (key, value) in measured.Values.Take(100)) Add(key, value);
            if (measured.Values.Count > 100) Add("显示限制", "仅显示前100个测量字段。");
        }
    }
}

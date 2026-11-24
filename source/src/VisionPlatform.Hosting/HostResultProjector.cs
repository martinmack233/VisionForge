using System.Text.Json;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Numerics;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Communication.Modbus;
using VisionPlatform.Communication.Serial;
using VisionPlatform.Runtime.Execution;

namespace VisionPlatform.Hosting;

/// <summary>Only bounded data snapshots cross the host boundary; never reflect unknown plugin objects.</summary>
public static class HostResultProjector
{
    private const int MaximumItems = 1000, MaximumText = 16384;
    private static string? Text(string? value) => value is null || value.Length <= MaximumText ? value : value[..MaximumText];
    public static HostResultSnapshot Project(Guid hostRunId, RunResult result)
    {
        var nodes = new List<HostNodeSnapshot>(); var bytes = 0;
        foreach (var node in result.Nodes.Values.Take(1000))
        {
            var outputs = new Dictionary<string, HostOutputSnapshot>();
            foreach (var pair in node.Outputs.Take(64))
            {
                var output = ProjectValue(pair.Value);
                bytes += System.Text.Encoding.UTF8.GetByteCount(output.Value.GetRawText()) + 512;
                if (bytes > 700000) throw new HostCommandException(VisionPlatform.Contracts.Hosting.HostErrorCodes.ResultTooLarge, "Projected outputs exceed the 700000-byte retained-result budget; narrow the graph/ROI or execute to a target node.");
                outputs[pair.Key] = output;
            }
            var row = new HostNodeSnapshot(node.NodeId, node.Status, node.SkipReason, node.ExecutionTime.TotalMilliseconds, Text(node.ErrorMessage), outputs);
            bytes += node.NodeId.Length * 3 + (node.ErrorMessage?.Length ?? 0) * 3 + 256;
            if (bytes > 700000) throw new HostCommandException(VisionPlatform.Contracts.Hosting.HostErrorCodes.ResultTooLarge, "Projected node data exceeds the retained-result budget.");
            nodes.Add(row);
        }
        return new(hostRunId, result.RunId, result.Status, result.Success, result.ExecutionTime.TotalMilliseconds, Text(result.ErrorMessage), nodes);
    }
    public static HostOutputSnapshot ProjectValue(object value)
    {
        object projected; var truncated = false;
        switch (value)
        {
            case ModbusConnectionReference reference:projected=reference;break;
            case ModbusResult modbus:projected=new{modbus.RequestId,modbus.Operation,modbus.UnitId,modbus.Address,modbus.TransactionId,Coils=modbus.Coils.ToArray(),Registers=modbus.Registers.ToArray(),modbus.Milliseconds,modbus.ResponseReceived,modbus.DuplicateSuppressed};break;
            case ModbusBitValues bits:projected=new{bits.Count,Values=bits.ToArray()};break;
            case ModbusRegisterValues words:projected=new{words.Count,Values=words.ToArray()};break;
            case BinaryData binary:truncated=binary.Count>1024;projected=new{binary.Count,Bytes=Enumerable.Range(0,Math.Min(binary.Count,1024)).Select(i=>(int)binary[i]).ToArray(),Encoding="raw bytes (JSON integer array)"};break;
            case TabularData table:truncated=TableTruncated(table);projected=Table(table);break;
            case FileReadResult file:truncated=(file.Text?.Length??0)>MaximumText||(file.Table is not null&&TableTruncated(file.Table))||(file.Bytes?.Count??0)>1024;projected=new{file.Path,file.Sha256,file.Format,file.Encoding,file.ByteCount,Text=Text(file.Text),Table=file.Table is null?null:Table(file.Table),BinaryCount=file.Bytes?.Count};break;
            case NumericMatrix matrix:
                truncated=matrix.Values.Count>1000;projected=new{matrix.Rows,matrix.Columns,Precision="Float64",Values=matrix.Values.Take(1000).ToArray()};break;
            case ProjectiveTransform2D transform:
                projected=new{transform.SourceSpaceId,transform.TargetSpaceId,transform.IsAffine,Matrix=new{transform.Matrix.Rows,transform.Matrix.Columns,transform.Matrix.Values},Direction="forward source pixel centers -> target pixel centers"};break;
            case FloatPlaneFrame plane:
                using(var lease=plane.Lease()){var samples=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,float>(lease.GetRowSpan(0))[..Math.Min(plane.Width,64)].ToArray();projected=new{plane.Width,plane.Height,plane.Stride,plane.FrameId,plane.CoordinateSpace,plane.Quantity,plane.Unit,Type="Float32",FirstRowSamples=samples,PreviewPort="Preview (8-bit display only)"};truncated=(long)plane.Width*plane.Height>samples.Length;}break;
            case EnhancementStatistics statistics:projected=statistics;break;
            case VisionCollection collection:
                truncated = CollectionTruncated(collection);
                projected = Collection(collection); break;
            case VisionItem item:
                projected = Item(item); break;
            case CollectionDisplayResult display:
                truncated = CollectionTruncated(display.Collection);
                projected = new { Collection = Collection(display.Collection), display.Options, Summary = Text(display.Summary) }; break;
            case IterationBatch batch:
                truncated = CollectionTruncated(batch.Results);
                projected = new { Results = Collection(batch.Results), batch.Complete, batch.Cancelled, batch.CompletedCount, Error = Text(batch.Error) }; break;
            case IterationTraceStore trace:
                projected = new { RetainedIterations = trace.Traces.Select(t => new { t.Index, t.RunId, NodeCount = t.Nodes.Count }), trace.RetainedBytes, Message = "Intermediate images are local bounded leases, never native pointers in host protocol." }; break;
            case ImageFrame f:
                projected = new { f.Width, f.Height, f.Stride, f.PixelFormat, f.FrameId, f.Timestamp, f.CoordinateSpace }; break;
            case ColorPlaneFrame f:
                projected = new { f.Width, f.Height, f.Stride, f.ColorSpace, f.FrameId, f.Timestamp, f.CoordinateSpace, f.StorageBytes, PreviewPort = "Preview (explicit BGR8 conversion)" }; break;
            case RegionStatistics statistics:
                projected = statistics; break;
            case CameraFrameInfo frame: projected = frame with { SourceName = Text(frame.SourceName)! }; truncated = frame.SourceName.Length > MaximumText; break;
            case TemplateModel t:
                projected = new { t.ModelId, t.ReferenceX, t.ReferenceY, t.Image.Width, t.Image.Height, t.Image.PixelFormat }; break;
            case string text: projected = Text(text)!; truncated = text.Length > MaximumText; break;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or Guid: projected = value; break;
            case double d when double.IsFinite(d): projected = d; break;
            case float f when float.IsFinite(f): projected = f; break;
            case RectRoi or RotatedRectRoi or Pose2D or Point2D or CoordinateTransform2D or ControlSignal or InspectionDecision or CommunicationStatus or TemplateMatchResult or LineGeometry or CircleGeometry or TcpConnectionReference or TcpServerReference or SerialPortReference:
                projected = value; break;
            case RectRoi[] rectangles: projected = rectangles.Take(MaximumItems).ToArray(); truncated = rectangles.Length > MaximumItems; break;
            case Point2D[] points: projected = points.Take(MaximumItems).ToArray(); truncated = points.Length > MaximumItems; break;
            case InspectionReport report:
                projected = report with { Measurement = report.Measurement is null ? null : Summary(report.Measurement) }; truncated = report.Measurement?.Values.Count > MaximumItems; break;
            case BlobResults b:
                truncated = b.Count > MaximumItems;
                projected = new { b.Count, b.CoordinateSpaceId, b.RasterWidth, b.RasterHeight, b.RasterOriginX, b.RasterOriginY,
                    Items = b.Items.Take(MaximumItems).Select(i => new { i.Label, i.Area, i.CenterX, i.CenterY, i.Bounds, i.Perimeter, i.Circularity }).ToArray() }; break;
            case MeasurementResult m:
                truncated = m.Values.Count > MaximumItems || m.Overlays.Count > 64 || m.Overlays.Take(64).Any(o => o.Points.Count > 64 || o.Label.Length > MaximumText);
                projected = new { m.Valid, m.Kind, m.CoordinateSpaceId, m.Unit, Values = m.Values.Take(MaximumItems).ToDictionary(p => p.Key, p => p.Value), ErrorReason = Text(m.ErrorReason),
                    Overlays = m.Overlays.Take(64).Select(o => o with { Label = Text(o.Label)!, Points = o.Points.Take(64).ToArray() }).ToArray() }; break;
            case MeasurementSummary m: projected = Summary(m); truncated = m.Values.Count > MaximumItems; break;
            case EdgePointSet p: projected = p with { Points = p.Points.Take(MaximumItems).ToArray() }; truncated = p.Points.Count > MaximumItems; break;
            case OcrResult o:
                truncated = o.Words.Count > MaximumItems || o.Text.Length > MaximumText || OverlayTruncated(o.Overlays) || o.Words.Take(MaximumItems).Any(w => w.Text.Length > MaximumText);
                projected = o with { Text = Text(o.Text)!, Words = o.Words.Take(MaximumItems).Select(w => w with { Text = Text(w.Text)! }).ToArray(), Overlays = Overlays(o.Overlays), FailureReason = Text(o.FailureReason) }; break;
            case CodeReadResult c:
                truncated = c.Codes.Count > MaximumItems || OverlayTruncated(c.Overlays) || c.Codes.Take(MaximumItems).Any(x => x.Text.Length > MaximumText || x.Points.Count > 64);
                projected = c with { Codes = c.Codes.Take(MaximumItems).Select(x => x with { Text = Text(x.Text)!, Points = x.Points.Take(64).ToArray() }).ToArray(), Overlays = Overlays(c.Overlays), FailureReason = Text(c.FailureReason) }; break;
            case YoloInferenceResult y:
                truncated = y.Detections.Count > MaximumItems || OverlayTruncated(y.Overlays) || y.Detections.Take(MaximumItems).Any(x => x.Label.Length > MaximumText);
                projected = y with { Detections = y.Detections.Take(MaximumItems).Select(x => x with { Label = Text(x.Label)! }).ToArray(), Overlays = Overlays(y.Overlays), FailureReason = Text(y.FailureReason) }; break;
            default:
                projected = new { Supported = false, Reason = "This plugin output type is not exposed by protocol v1." }; break;
        }
        try { return new(value.GetType().Name, JsonSerializer.SerializeToElement(projected, JsonDefaults.Options), truncated); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { return new(value.GetType().Name, JsonDefaults.Element(new { Supported = false, Reason = "Output could not be represented as finite protocol data." })); }
    }
    private static string Cell(string value)=>value.Length<=128?value:value[..128];
    private static bool TableTruncated(TabularData table)=>table.RowCount>20||table.ColumnCount>20||table.Headers.Any(h=>h.Length>128)||table.Rows.Take(20).Any(r=>r.Take(20).Any(v=>v.Length>128));
    private static object Table(TabularData table)=>new{table.RowCount,table.ColumnCount,Headers=table.Headers.Take(20).Select(Cell).ToArray(),Rows=table.Rows.Take(20).Select(r=>r.Take(20).Select(Cell).ToArray()).ToArray()};
    private static MeasurementSummary Summary(MeasurementSummary m) => m with { Values = m.Values.Take(MaximumItems).ToDictionary(p => p.Key, p => p.Value), ErrorReason = Text(m.ErrorReason) };
    private static object Collection(VisionCollection c) => new { c.Kind, c.Count, Items = c.Items.Take(MaximumItems).Select(Item).ToArray() };
    private static bool CollectionTruncated(VisionCollection c) => c.Count>MaximumItems || c.Items.Take(MaximumItems).OfType<ContourItem>().Any(i=>i.Points.Count>128);
    private static object Item(VisionItem i) => new { i.Kind, i.Origin, i.State, i.Region,
        Fields = i.Fields.Take(128).ToDictionary(p => p.Key, p => p.Value with { Text = Text(p.Value.Text) }),
        Contour = i is ContourItem contour ? new { Points=contour.Points.Take(128).ToArray(), contour.Closed, contour.ParentIndex, contour.IsHole, Ellipse=contour.Analysis?.Ellipse } : null,
        Line = i is LineItem line ? new { line.Line, line.EndPoint } : null,
        Circle = i is CircleItem circle ? circle.Circle : null,
        Iteration = i is IterationItem iteration ? new { iteration.Index, iteration.IterationRunId, iteration.Status, Milliseconds = iteration.ExecutionTime.TotalMilliseconds, Error = Text(iteration.Error) } : null };
    private static IReadOnlyList<RecognitionOverlay> Overlays(IReadOnlyList<RecognitionOverlay> overlays) => overlays.Take(256)
        .Select(o => o with { Label = Text(o.Label)!, Points = o.Points.Take(64).ToArray() }).ToArray();
    private static bool OverlayTruncated(IReadOnlyList<RecognitionOverlay> overlays) => overlays.Count > 256 || overlays.Take(256).Any(o => o.Points.Count > 64 || o.Label.Length > MaximumText);
}

using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class BarcodeReaderNode : NodeBase<BarcodeReadOptions>
{
    public override NodeDescriptor Descriptor => Describe("BarcodeReader", [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("Result", typeof(CodeReadResult)), new("Text", typeof(string)), new("Format", typeof(string)), new("Success", typeof(bool)), new("Count", typeof(int))]);
    protected override IReadOnlyList<string> Validate(BarcodeReadOptions options) => ZxingCodeReader.Validate(options);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        context.TryInput<RotatedRectRoi>("ROI", out var roi);
        var result = new ZxingCodeReader().Read(context.Input<ImageFrame>("Image"), context.Parameters<BarcodeReadOptions>(), roi, cancellationToken);
        return ValueTask.FromResult(BarcodeNodeOutputs.From(result));
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class QRCodeReaderNode : NodeBase<QrCodeReadOptions>
{
    public override NodeDescriptor Descriptor => Describe("QRCodeReader", [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("Result", typeof(CodeReadResult)), new("Text", typeof(string)), new("Format", typeof(string)), new("Success", typeof(bool)), new("Count", typeof(int))]);
    protected override IReadOnlyList<string> Validate(QrCodeReadOptions options) => ZxingCodeReader.Validate(options);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        context.TryInput<RotatedRectRoi>("ROI", out var roi);
        var result = new ZxingCodeReader(qrCodeOnly: true).Read(context.Input<ImageFrame>("Image"), context.Parameters<QrCodeReadOptions>(), roi, cancellationToken);
        return ValueTask.FromResult(BarcodeNodeOutputs.From(result));
    }
}

internal static class BarcodeNodeOutputs
{
    public static NodeOutput From(CodeReadResult result) => new(new Dictionary<string, object>
    {
        ["Result"] = result, ["Text"] = result.Text, ["Format"] = result.Format,
        ["Success"] = result.Success, ["Count"] = result.Codes.Count
    });
}

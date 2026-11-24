using VisionPlatform.Algorithms.Recognition;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe("ROI")]
public sealed class OcrNode : NodeBase<OcrOptions>
{
    private readonly IOcrRecognizer _recognizer;
    public OcrNode() : this(new TesseractOcrRecognizer()) { }
    public OcrNode(IOcrRecognizer recognizer) => _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    public override NodeDescriptor Descriptor => Describe("OCR",
        [new("Image", typeof(ImageFrame)), new("ROI", typeof(RotatedRectRoi), false)],
        [new("Result", typeof(OcrResult)), new("Text", typeof(string)), new("Confidence", typeof(double)), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(OcrOptions p) => TesseractOcrRecognizer.Validate(p);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var options = context.Parameters<OcrOptions>();
        if (!string.IsNullOrWhiteSpace(options.DataPath)) options = options with
        {
            DataPath = Path.GetFullPath(options.DataPath, Path.GetFullPath(context.ResourceRoot))
        };
        var roi = context.TryInput<RotatedRectRoi>("ROI", out var supplied) ? supplied : null;
        var result = _recognizer.Recognize(context.Input<ImageFrame>("Image"), options, roi, cancellationToken);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["Result"] = result, ["Text"] = result.Text, ["Confidence"] = result.Confidence, ["Success"] = result.Success
        }));
    }
}

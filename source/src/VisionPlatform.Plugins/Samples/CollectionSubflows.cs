using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Algorithms.Recognition;

namespace VisionPlatform.Plugins.Samples;
public static class CollectionSubflows
{
    public static SubflowDefinition Ocr() => Layout(new()
    {
        Nodes = [N("current", "IterationInput", new { }), N("roi", "ItemToRoi", new { }),
            N("ocr", "OCR", new OcrOptions { Segmentation = OcrSegmentation.SingleLine }), N("collect", "IterationOutput", new { })],
        Connections = [E("current", "Item", "roi", "Item"), E("current", "Image", "ocr", "Image"), E("roi", "ROI", "ocr", "ROI"),
            E("ocr", "Result", "collect", "OCR"), E("roi", "ROI", "collect", "ROI")]
    });
    public static SubflowDefinition Measure() => Layout(new()
    {
        Nodes = [N("current", "IterationInput", new { }), N("roi", "ItemToRoi", new { }), N("measure", "DistanceMeasure", new { }),
            N("judge", "Compare", new { ValueB = 12d, Operator = "GreaterOrEqual" }), N("collect", "IterationOutput", new { })],
        Connections = [E("current", "Item", "roi", "Item"), E("roi", "Center", "measure", "PointA"), E("roi", "Right", "measure", "PointB"),
            E("measure", "Distance", "judge", "ValueA"), E("measure", "Result", "collect", "Measurement"), E("judge", "Result", "collect", "IsOk"),
            E("measure", "Distance", "collect", "Number"), E("roi", "ROI", "collect", "ROI")]
    });
    private static SubflowDefinition Layout(SubflowDefinition body) => body with { NodeLocations = body.Nodes.Select((n, i) => (n, i)).ToDictionary(p => p.n.NodeId, p => new Point2D(p.i * 380, 60)) };
    public static NodeSpecification N(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    public static NodeConnection E(string source, string output, string target, string input) => new(source, output, target, input);
}

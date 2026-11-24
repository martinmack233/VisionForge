using System.Text.RegularExpressions;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;
[SubflowSafe]
public sealed class CollectionDisplayNode : NodeBase<CollectionDisplayOptions>
{
    public override NodeDescriptor Descriptor => Describe("CollectionDisplay", [new("Collection", typeof(VisionCollection)), new("Image", typeof(ImageFrame), false)],
        [new("Display", typeof(CollectionDisplayResult)), new("Collection", typeof(VisionCollection)), new("Image", typeof(ImageFrame), false), new("Summary", typeof(string))]);
    protected override IReadOnlyList<string> Validate(CollectionDisplayOptions p)
    {
        var errors = new List<string>();
        foreach (var value in new[] { p.TextColor, p.BackgroundColor, p.LineColor, p.FillColor, p.OkColor, p.NgColor, p.UnrecognizedColor, p.ErrorColor })
            if (value is null || !Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$")) errors.Add("Colors must use #RRGGBB.");
        if (p.Fields is null || p.Fields.Length > 512 || !Regex.IsMatch(p.Fields, "^[A-Za-z0-9_, .-]*$")) errors.Add("Choose supported field names separated by commas; scripts/format expressions are not supported.");
        if (p.DecimalPlaces is < 0 or > 12 || p.SummaryLines is < 1 or > 8 || p.MaximumOverlays is < 0 or > 2000 ||
            !double.IsFinite(p.FontSize) || p.FontSize is < 6 or > 72 || !double.IsFinite(p.LineWidth) || p.LineWidth is < .1 or > 12 ||
            !double.IsFinite(p.FillOpacity) || p.FillOpacity is < 0 or > 1 || !double.IsFinite(p.BackgroundOpacity) || p.BackgroundOpacity is < 0 or > 1 ||
            !double.IsFinite(p.OffsetX) || !double.IsFinite(p.OffsetY) || Math.Abs(p.OffsetX) > 10000 || Math.Abs(p.OffsetY) > 10000 || !Enum.IsDefined(p.Position) || !Enum.IsDefined(p.State)) errors.Add("Invalid display limits/position/opacity.");
        return errors;
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var collection = c.Input<VisionCollection>("Collection"); var display = new CollectionDisplayResult(collection, c.Parameters<CollectionDisplayOptions>());
        var values = new Dictionary<string, object> { ["Display"] = display, ["Collection"] = collection, ["Summary"] = display.Summary };
        ImageFrame? image = null;
        if (c.TryInput<ImageFrame>("Image", out var input))
        {
            if (collection.Items.Any(i => i.Origin.CoordinateSpaceId != input.CoordinateSpace.Id || i.Origin.FrameId is { } id && id != input.FrameId))
                throw new ArgumentException("Display image and collection frame/coordinate space differ; transform explicitly before overlay.");
            image = input.Share(); values["Image"] = image;
        }
        return ValueTask.FromResult(new NodeOutput(values, image is null ? [] : [image]));
    }
}

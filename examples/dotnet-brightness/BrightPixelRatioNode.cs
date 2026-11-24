using System.Text.Json;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionForge.Examples;

public sealed class BrightPixelRatioNode : IExecutableNode
{
    private const int DefaultThreshold = 128;

    public NodeDescriptor Descriptor { get; } = new(
        "Example.BrightPixelRatio", "1.0.0",
        [new PortDefinition("Image", typeof(ImageFrame))],
        [
            new PortDefinition("BrightPixels", typeof(long)),
            new PortDefinition("TotalPixels", typeof(long)),
            new PortDefinition("Ratio", typeof(double))
        ]);

    public IReadOnlyList<string> ValidateParameters(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            return ["Parameters must be a JSON object."];
        if (parameters.TryGetProperty("Threshold", out var value) &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var threshold) ||
             threshold is < 0 or > 255))
            return ["Threshold must be an integer from 0 to 255."];
        return [];
    }

    public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var parameters = context.Specification.Parameters;
        var errors = ValidateParameters(parameters);
        if (errors.Count != 0)
            throw new ArgumentException(errors[0]);
        var threshold = parameters.TryGetProperty("Threshold", out var value)
            ? value.GetInt32()
            : DefaultThreshold;
        var image = context.Input<ImageFrame>("Image");
        if (image.PixelFormat != PixelFormat.Mono8)
            throw new ArgumentException("This example expects a Mono8 image.");

        long bright = 0;
        using var pixels = image.Lease();
        for (var row = 0; row < image.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pixel in pixels.GetRowSpan(row)[..image.Width])
            {
                if (pixel >= threshold)
                    bright++;
            }
        }

        var total = (long)image.Width * image.Height;
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        {
            ["BrightPixels"] = bright,
            ["TotalPixels"] = total,
            ["Ratio"] = (double)bright / total
        }));
    }
}

using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Imaging;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Builtin;

public sealed record ImageInputParameters
{
    public string Path { get; init; } = "";
    public PixelFormat? PixelFormat { get; init; }
}
public sealed class ImageInputNode : NodeBase<ImageInputParameters>
{
    public override NodeDescriptor Descriptor => Describe("ImageInput",
        [new("Image", typeof(ImageFrame), false)], [new("Image", typeof(ImageFrame))]);
    protected override IReadOnlyList<string> Validate(ImageInputParameters p) =>
        p.PixelFormat is { } f && !Enum.IsDefined(f) ? ["Invalid pixel format."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<ImageInputParameters>();
        var image = context.TryInput<ImageFrame>("Image", out var supplied) ? supplied.Share()
            : ImageFile.Load(ProjectStore.ResolveResourcePath(context.ResourceRoot, p.Path), p.PixelFormat);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Image"] = image }, [image]));
    }
}

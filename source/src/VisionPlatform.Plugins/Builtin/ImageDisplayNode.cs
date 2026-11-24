using System.ComponentModel;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record ImageDisplayParameters
{
    [DisplayName("显示结果叠加")]
    public bool ShowOverlays { get; init; } = true;
}

/// <summary>A headless presentation endpoint. UI hosts consume its ordinary image/geometry outputs.</summary>
public sealed class ImageDisplayNode : NodeBase<ImageDisplayParameters>
{
    public override NodeDescriptor Descriptor => Describe("ImageDisplay",
        [new("Image", typeof(ImageFrame)), new("Blobs", typeof(BlobResults), false), new("ROI", typeof(RotatedRectRoi), false)],
        [new("Image", typeof(ImageFrame)), new("Blobs", typeof(BlobResults), false), new("ROI", typeof(RotatedRectRoi), false)]);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = context.Input<ImageFrame>("Image");
        var showOverlays = context.Parameters<ImageDisplayParameters>().ShowOverlays;
        var values = new Dictionary<string, object>();
        if (showOverlays && context.TryInput<BlobResults>("Blobs", out var blobs))
        {
            if (blobs.CoordinateSpaceId != image.CoordinateSpace.Id)
                throw new ArgumentException("Display blobs and image must use the same coordinate space. Map cropped results explicitly before displaying on the original image.");
            values["Blobs"] = blobs;
        }
        if (showOverlays && context.TryInput<RotatedRectRoi>("ROI", out var roi))
        {
            roi.Validate();
            if (roi.CoordinateSpaceId != image.CoordinateSpace.Id)
                throw new ArgumentException("Display ROI and image must use the same coordinate space.");
            values["ROI"] = roi;
        }
        var shared = image.Share();
        values["Image"] = shared;
        return ValueTask.FromResult(new NodeOutput(values, [shared]));
    }
}

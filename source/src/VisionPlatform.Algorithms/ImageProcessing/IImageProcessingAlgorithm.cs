using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Algorithms.ImageProcessing;

/// <summary>Extension point for independent filters, enhancement, threshold, morphology and geometry algorithms.
/// Implementations borrow read-only input and return a new owned frame (or document an explicit shared result).</summary>
public interface IImageProcessingAlgorithm<in TOptions>
{
    string AlgorithmId { get; }
    IReadOnlyList<string> Validate(TOptions options);
    ImageFrame Process(ImageFrame image, TOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default);
}

public sealed class GaussianBlurAlgorithm : IImageProcessingAlgorithm<GaussianBlurOptions>
{
    public string AlgorithmId => "opencv.gaussian";
    public IReadOnlyList<string> Validate(GaussianBlurOptions options) => GaussianBlurProcessor.Validate(options);
    public ImageFrame Process(ImageFrame image, GaussianBlurOptions options, RectRoi? roi = null, CancellationToken cancellationToken = default)
        => GaussianBlurProcessor.Process(image, options, roi, cancellationToken);
}

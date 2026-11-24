using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;

namespace VisionPlatform.Contracts.Matching;

public interface ITemplateMatcher
{
    string AlgorithmId { get; }
    string Version { get; }
    IReadOnlyList<string> ValidateOptions(TemplateMatchOptions options);
    TemplateMatchResult Match(ImageFrame image, TemplateModel template, TemplateMatchOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class TemplateModel : IDisposable
{
    public ImageFrame Image { get; }
    public string ModelId { get; }
    public double ReferenceX { get; }
    public double ReferenceY { get; }
    public TemplateModel(ImageFrame image, string modelId = "template", double? referenceX = null, double? referenceY = null)
    {
        ReferenceX = referenceX ?? (image.Width - 1) / 2.0;
        ReferenceY = referenceY ?? (image.Height - 1) / 2.0;
        if (!double.IsFinite(ReferenceX) || !double.IsFinite(ReferenceY) || ReferenceX < 0 || ReferenceY < 0 ||
            ReferenceX > image.Width - 1 || ReferenceY > image.Height - 1) throw new ArgumentOutOfRangeException(nameof(referenceX));
        Image = image.Share(); ModelId = modelId;
    }
    public void Dispose() => Image.Dispose();
}

public sealed record TemplateMatchOptions
{
    public double MinimumScore { get; init; } = .7;
    public double AngleMinDegrees { get; init; } = -20;
    public double AngleMaxDegrees { get; init; } = 20;
    public double AngleStepDegrees { get; init; } = 5;
    public double FineAngleStepDegrees { get; init; } = .25;
    public double ScaleMin { get; init; } = .8;
    public double ScaleMax { get; init; } = 1.2;
    public double ScaleStep { get; init; } = .05;
    public double CoarseScale { get; init; } = .5;
    public RectRoi? SearchRoi { get; init; }
}

public sealed record TemplateMatchResult(bool Success, double Score, double CenterX, double CenterY,
    double AngleDegrees, double Scale, RotatedRectRoi? MatchedRegion, TimeSpan ExecutionTime,
    string? FailureReason = null)
{
    public Pose2D? Pose => Success ? new(CenterX, CenterY, AngleDegrees, Scale,
        MatchedRegion?.CoordinateSpaceId ?? "image") : null;
}

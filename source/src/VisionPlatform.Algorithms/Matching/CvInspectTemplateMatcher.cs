using System.Diagnostics;
using CvInspect.Vision;
using CvInspect.Vision.Opts;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Imaging;

namespace VisionPlatform.Algorithms.Matching;

/// <summary>Thin adapter over fixed CvInspect 0.34.0. SearchRoi is a full-template search window in original pixels.</summary>
public sealed class CvInspectTemplateMatcher : ITemplateMatcher
{
    public string AlgorithmId => "cvinspect.pattern";
    public string Version => "0.34.0+0eddfbaa";

    public IReadOnlyList<string> ValidateOptions(TemplateMatchOptions options)
    {
        try { Validate(options); return []; }
        catch (ArgumentException ex) { return [ex.Message]; }
    }

    public TemplateMatchResult Match(ImageFrame image, TemplateModel template, TemplateMatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(options);
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        TemplateMatchResult Fail(string message, double score = 0) => new(false, score, 0, 0, 0, 0, null, timer.Elapsed, message);
        try
        {
            Validate(options);
            if (image.PixelFormat.IsBayer() || template.Image.PixelFormat.IsBayer())
                return Fail("Bayer inputs require an explicit RGB8/BGR8 conversion before template matching.");
            if (template.Image.Width < 4 || template.Image.Height < 4)
                return Fail("Template must be at least 4 x 4 pixels.");

            var x = 0; var y = 0; var width = image.Width; var height = image.Height;
            if (options.SearchRoi is { } roi)
            {
                var lx = roi.X - image.CoordinateSpace.OriginX;
                var ly = roi.Y - image.CoordinateSpace.OriginY;
                if (new[] { lx, ly, roi.Width, roi.Height }.Any(v => !double.IsFinite(v)) ||
                    lx < 0 || ly < 0 || roi.Width <= 0 || roi.Height <= 0 ||
                    lx + roi.Width > image.Width || ly + roi.Height > image.Height)
                    return Fail("Search ROI must be finite, nonempty and entirely inside the input image.");
                x = (int)Math.Floor(lx); y = (int)Math.Floor(ly);
                width = (int)Math.Ceiling(lx + roi.Width) - x;
                height = (int)Math.Ceiling(ly + roi.Height) - y;
            }
            using var search = image.CreateView(x, y, width, height);
            using var gray = PixelFormatConverter.Convert(search, PixelFormat.Mono8);
            using var templateGray = PixelFormatConverter.Convert(template.Image, PixelFormat.Mono8);
            using var inputLease = new OpenCvImageLease(gray.Image);
            using var templateLease = new OpenCvImageLease(templateGray.Image);
            var opt = new CvPatternOpt
            {
                Trained = true, TrainedShape = CvTrainShape.Rect,
                TrainedOriginX = template.ReferenceX, TrainedOriginY = template.ReferenceY,
                TrainedAngleDeg = (options.AngleMinDegrees + options.AngleMaxDegrees) / 2,
                UseAngleSearch = options.AngleMaxDegrees > options.AngleMinDegrees,
                AngleZoneDeg = (options.AngleMaxDegrees - options.AngleMinDegrees) / 2,
                CoarseStepDeg = options.AngleStepDegrees, FineStepDeg = options.FineAngleStepDegrees,
                UseScaleSearch = options.ScaleMin != 1 || options.ScaleMax != 1,
                ScaleZonePct = (options.ScaleMax - 1) * 100,
                ScaleStepPct = options.ScaleStep * 100, CoarseScale = options.CoarseScale,
                AcceptScore = options.MinimumScore
            };
            var pose = CvPatternFinder.Match(inputLease.Mat, templateLease.Mat, opt);
            cancellationToken.ThrowIfCancellationRequested();
            if (pose is not { } p || !p.IsValid || !double.IsFinite(p.Score))
                return Fail("Template has insufficient texture or no valid candidate fits the search window.");
            if (p.Score < options.MinimumScore) return Fail("Best candidate is below the matching score threshold.", p.Score);

            var centerX = p.FoundX + search.CoordinateSpace.OriginX;
            var centerY = p.FoundY + search.CoordinateSpace.OriginY;
            var region = new RotatedRectRoi(centerX, centerY, template.Image.Width * p.Scale,
                template.Image.Height * p.Scale, p.ThetaDeg, image.CoordinateSpace.Id);
            // Upstream constrains centers and may accept a partly clipped target. This adapter requires the
            // pixel-center template footprint to fit the requested search window, matching platform semantics.
            var footprint = region with { Width = (template.Image.Width - 1) * p.Scale,
                Height = (template.Image.Height - 1) * p.Scale };
            const double boundaryEpsilon = 1e-7;
            if (footprint.Corners().Any(v => v.X < search.CoordinateSpace.OriginX - boundaryEpsilon ||
                v.Y < search.CoordinateSpace.OriginY - boundaryEpsilon || v.X > search.CoordinateSpace.OriginX + width - 1 + boundaryEpsilon ||
                v.Y > search.CoordinateSpace.OriginY + height - 1 + boundaryEpsilon))
                return Fail("Candidate template extends beyond the search ROI.", p.Score);

            // ReferenceX/Y persist teaching metadata. Runtime poses consistently use the geometric template
            // center, independent of a custom editor anchor.
            return new(true, p.Score, centerX, centerY, p.ThetaDeg, p.Scale, region, timer.Elapsed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OpenCvSharp.OpenCVException or NotSupportedException)
        { return Fail(ex.Message); }
    }

    public static void Validate(TemplateMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var numbers = new[] { options.MinimumScore, options.AngleMinDegrees, options.AngleMaxDegrees,
            options.AngleStepDegrees, options.FineAngleStepDegrees, options.ScaleMin, options.ScaleMax,
            options.ScaleStep, options.CoarseScale };
        if (numbers.Any(v => !double.IsFinite(v))) throw new ArgumentException("Matching parameters must be finite.");
        if (options.MinimumScore is < 0 or > 1) throw new ArgumentException("MinimumScore must be within [0, 1].");
        if (options.AngleMinDegrees < -360 || options.AngleMaxDegrees > 360 || options.AngleMinDegrees > options.AngleMaxDegrees ||
            options.AngleStepDegrees < .5 || options.FineAngleStepDegrees < .1 || options.FineAngleStepDegrees > options.AngleStepDegrees)
            throw new ArgumentException("Invalid angle range or steps (coarse >= 0.5 degrees, fine >= 0.1 degrees).");
        if (options.ScaleMin < .5 || options.ScaleMax > 1.5 || options.ScaleMin > options.ScaleMax ||
            Math.Abs(options.ScaleMin + options.ScaleMax - 2) > 1e-8 || options.ScaleStep is < .005 or > .25)
            throw new ArgumentException("CvInspect supports scale ranges symmetric about 1.0 within [0.5, 1.5], step [0.005, 0.25].");
        var count = (options.ScaleMax - 1) / options.ScaleStep;
        if (Math.Abs(count - Math.Round(count)) > 1e-7)
            throw new ArgumentException("Scale range endpoints must lie on the configured scale grid.");
        if (options.CoarseScale is < .1 or > 1) throw new ArgumentException("CoarseScale must be within [0.1, 1].");
        if ((options.AngleMaxDegrees - options.AngleMinDegrees) / options.AngleStepDegrees > 1440)
            throw new ArgumentException("Too many angle candidates.");
    }
}

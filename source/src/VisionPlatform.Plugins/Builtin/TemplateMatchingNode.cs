using VisionPlatform.Algorithms.Matching;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Plugins.Builtin;

/// <summary>Additional engines register factories without changing the Runtime or the node.</summary>
public sealed class MatcherCatalog
{
    private readonly Dictionary<string, Func<ITemplateMatcher>> _factories = new(StringComparer.Ordinal);
    public MatcherCatalog() => Register(() => new CvInspectTemplateMatcher());
    public void Register(Func<ITemplateMatcher> factory)
    {
        var instance = factory();
        if (!_factories.TryAdd(instance.AlgorithmId, factory)) throw new ArgumentException("Duplicate matcher: " + instance.AlgorithmId);
    }
    public ITemplateMatcher Create(string algorithmId) => _factories.TryGetValue(algorithmId, out var factory)
        ? factory() : throw new KeyNotFoundException("Template matching algorithm is unavailable: " + algorithmId);
}
public sealed record TemplateMatchingParameters
{
    public string AlgorithmId { get; init; } = "cvinspect.pattern";
    public string TemplatePath { get; init; } = "";
    public TemplateMatchOptions Options { get; init; } = new();
}
public sealed class TemplateMatchingNode : NodeBase<TemplateMatchingParameters>
{
    private readonly MatcherCatalog _matchers;
    public TemplateMatchingNode() : this(new MatcherCatalog()) { }
    public TemplateMatchingNode(MatcherCatalog matchers) => _matchers = matchers;
    public override NodeDescriptor Descriptor => Describe("TemplateMatching",
        [new("Image", typeof(ImageFrame)), new("Template", typeof(TemplateModel), false), new("SearchROI", typeof(RectRoi), false)],
        [new("MatchResult", typeof(TemplateMatchResult)), new("Pose2D", typeof(Pose2D), false), new("Success", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(TemplateMatchingParameters p)
    {
        try { return _matchers.Create(p.AlgorithmId).ValidateOptions(p.Options); }
        catch (Exception ex) { return [ex.Message]; }
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var p = context.Parameters<TemplateMatchingParameters>();
        TemplateModel? owned = null;
        try
        {
            var template = context.TryInput<TemplateModel>("Template", out var supplied) ? supplied
                : owned = TemplateResourceStore.Load(ProjectStore.ResolveResourcePath(context.ResourceRoot, p.TemplatePath));
            var options = context.TryInput<RectRoi>("SearchROI", out var roi) ? p.Options with { SearchRoi = roi } : p.Options;
            var match = _matchers.Create(p.AlgorithmId).Match(context.Input<ImageFrame>("Image"), template, options, cancellationToken);
            var values = new Dictionary<string, object> { ["MatchResult"] = match, ["Success"] = match.Success };
            if (match.Pose is { } pose) values["Pose2D"] = pose;
            return ValueTask.FromResult(new NodeOutput(values));
        }
        finally { owned?.Dispose(); }
    }
}

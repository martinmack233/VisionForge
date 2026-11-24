using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record ResultDecisionParameters { public int MinimumBlobCount { get; init; } = 2; public int MaximumBlobCount { get; init; } = 2; }
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class ResultDecisionNode : NodeBase<ResultDecisionParameters>
{
    public override NodeDescriptor Descriptor => Describe("ResultDecision",
        [new("BlobCount", typeof(int)), new("MatchSuccess", typeof(bool), false)],
        [new("Decision", typeof(InspectionDecision)), new("IsOk", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(ResultDecisionParameters p) =>
        p.MinimumBlobCount < 0 || p.MaximumBlobCount < p.MinimumBlobCount ? ["Invalid decision Blob count range."] : [];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<ResultDecisionParameters>();
        var count = context.Input<int>("BlobCount");
        var matched = !context.TryInput<bool>("MatchSuccess", out var ok) || ok;
        var accepted = matched && count >= p.MinimumBlobCount && count <= p.MaximumBlobCount;
        var decision = new InspectionDecision(accepted, !matched ? "Template matching failed."
            : $"BlobCount={count}; accepted range [{p.MinimumBlobCount}, {p.MaximumBlobCount}].");
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Decision"] = decision, ["IsOk"] = accepted }));
    }
}

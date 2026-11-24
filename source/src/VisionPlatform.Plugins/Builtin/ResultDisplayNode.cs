using System.ComponentModel;
using VisionPlatform.Algorithms.Results;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Measurement;

namespace VisionPlatform.Plugins.Builtin;

public sealed record ResultDisplayParameters
{
    [DisplayName("结果格式化")] public ResultFormatOptions Options { get; init; } = new();
}

/// <summary>A structured presentation endpoint; it executes no UI or transport operations.</summary>
public sealed class ResultDisplayNode : NodeBase<ResultDisplayParameters>
{
    public override NodeDescriptor Descriptor => Describe("ResultDisplay",
        [new("Decision", typeof(InspectionDecision), false), new("IsOk", typeof(bool), false), new("BlobCount", typeof(int), false),
         new("MatchResult", typeof(TemplateMatchResult), false), new("Pose", typeof(Pose2D), false),
         new("ElapsedMilliseconds", typeof(double), false), new("Communication", typeof(CommunicationStatus), false), new("Measurement", typeof(MeasurementResult), false)],
        [new("Result", typeof(InspectionReport)), new("Message", typeof(string)), new("IsOk", typeof(bool))]);

    protected override IReadOnlyList<string> Validate(ResultDisplayParameters parameters) => ResultFormatter.Validate(parameters.Options);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasDecision = context.TryInput<InspectionDecision>("Decision", out var decision);
        var hasOk = context.TryInput<bool>("IsOk", out var isOk);
        var hasMeasurement = context.TryInput<MeasurementResult>("Measurement", out var measurement);
        if (!hasDecision && !hasOk && !hasMeasurement) throw new ArgumentException("ResultDisplay requires Decision or IsOk, or a Measurement from this execution.");
        if (hasDecision && hasOk && decision.IsOk != isOk) throw new ArgumentException("Decision and IsOk disagree.");
        if (hasDecision) isOk = decision.IsOk;
        else if (!hasOk && hasMeasurement) isOk = measurement.Valid;
        var hasMatch = context.TryInput<TemplateMatchResult>("MatchResult", out var match);
        var hasPose = context.TryInput<Pose2D>("Pose", out var suppliedPose);
        Pose2D? pose = hasPose ? suppliedPose : null;
        if (hasMatch && !match.Success && hasPose) throw new ArgumentException("A failed match cannot supply a valid result pose.");
        if (!hasPose && hasMatch) pose = match.Pose;
        var report = new InspectionReport
        {
            InspectionId = context.RunId == Guid.Empty ? Guid.NewGuid() : context.RunId,
            IsOk = isOk,
            BlobCount = context.TryInput<int>("BlobCount", out var count) ? count : null,
            MatchScore = hasMatch ? match.Score : null,
            CenterX = pose?.CenterX, CenterY = pose?.CenterY, Angle = pose?.AngleDegrees, Scale = pose?.Scale,
            ExecutionMilliseconds = context.TryInput<double>("ElapsedMilliseconds", out var elapsed) ? elapsed : hasMatch ? match.ExecutionTime.TotalMilliseconds : null,
            Communication = context.TryInput<CommunicationStatus>("Communication", out var communication) ? communication : null
            , Measurement = hasMeasurement ? measurement.Summary : null
        };
        var text = ResultFormatter.Format(report, context.Parameters<ResultDisplayParameters>().Options);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = report, ["Message"] = text, ["IsOk"] = isOk }));
    }
}

public sealed class ResultFormatterNode : NodeBase<ResultFormatOptions>
{
    public override NodeDescriptor Descriptor => Describe("ResultFormatter", [new("Result", typeof(InspectionReport))], [new("Message", typeof(string))]);
    protected override IReadOnlyList<string> Validate(ResultFormatOptions parameters) => ResultFormatter.Validate(parameters);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = ResultFormatter.Format(context.Input<InspectionReport>("Result"), context.Parameters<ResultFormatOptions>());
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Message"] = text }));
    }
}

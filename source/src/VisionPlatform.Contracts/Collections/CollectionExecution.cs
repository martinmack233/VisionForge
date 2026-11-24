using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Contracts.Collections;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SubflowSafeAttribute(params string[] requiredInputs) : Attribute
{
    public IReadOnlyList<string> RequiredInputs { get; } = requiredInputs;
}
public enum ItemFailurePolicy { Stop, Continue }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubflowDefinition
{
    public int Version { get; init; } = 1;
    public string InputNodeId { get; init; } = "current";
    public string OutputNodeId { get; init; } = "collect";
    public List<NodeSpecification> Nodes { get; init; } = [];
    public List<NodeConnection> Connections { get; init; } = [];
    public Dictionary<string, Point2D> NodeLocations { get; init; } = [];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForEachOptions
{
    [Browsable(false)] public SubflowDefinition Body { get; init; } = new();
    [DisplayName("异常策略")] public ItemFailurePolicy FailurePolicy { get; init; } = ItemFailurePolicy.Stop;
    [DisplayName("最多项目数"), Range(1, 10000)] public int MaximumItems { get; init; } = 100;
    [DisplayName("工作图像预算 MB"), Range(1, 2048)] public int WorkingImageMegabytes { get; init; } = 256;
    [DisplayName("保留调试迭代数"), Range(0, 100)] public int DebugIterations { get; init; } = 3;
    [DisplayName("调试图像预算 MB"), Range(0, 512)] public int DebugImageMegabytes { get; init; } = 32;
}
public sealed record ItemComputation(string Text, double? Number, double? Confidence, BusinessState State,
    RotatedRectRoi? Region, MeasurementResult? Measurement, string? Message);
public enum IterationStatus { Completed, Failed, Cancelled, NotStarted }
public sealed record IterationItem(ItemOrigin Source, VisionItem SourceItem, int Index, Guid IterationRunId,
    IterationStatus Status, TimeSpan ExecutionTime, ItemComputation? Result, string? Error) : VisionItem(Source)
{
    public override VisionItemKind Kind => VisionItemKind.Iteration;
    public override RotatedRectRoi? Region => Result?.Region ?? SourceItem.Region;
    public override BusinessState State => Status == IterationStatus.Completed ? Result?.State ?? BusinessState.Unrecognized
        : Status == IterationStatus.Failed ? BusinessState.Error : BusinessState.Unfinished;
    public override IReadOnlyDictionary<string, CollectionValue> Fields
    {
        get
        {
            var pairs = new List<(string, CollectionValue)> { ("Index", Index), ("Text", Result?.Text ?? ""),
                ("Status", Status.ToString()), ("State", State.ToString()), ("Milliseconds", ExecutionTime.TotalMilliseconds), ("Error", Error ?? Result?.Message ?? "") };
            if (Result?.Confidence is { } c) pairs.Add(("Confidence", c));
            if (Result?.Number is { } n) pairs.Add(("Number", n));
            if (Region is { } r) pairs.AddRange(Geometry(r));
            if (Result?.Measurement is { } m) pairs.AddRange(m.Values.Where(v => !pairs.Any(p => p.Item1 == v.Key)).Select(v => (v.Key, (CollectionValue)v.Value)));
            return Data(pairs.ToArray());
        }
    }
}
public sealed record IterationBatch(VisionCollection Results, bool Complete, bool Cancelled, string? Error)
{
    public int CompletedCount => Results.Items.Cast<IterationItem>().Count(i => i.Status == IterationStatus.Completed);
    public int FailedCount => Results.Items.Cast<IterationItem>().Count(i => i.Status == IterationStatus.Failed);
    public bool AllExecutedSuccessfully => Complete && !Cancelled && FailedCount == 0;
}

using System.Diagnostics;
using System.Text.Json;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Measurement;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Runtime.Execution;

namespace VisionPlatform.Plugins.Builtin;

public sealed record IterationInputOptions { [System.ComponentModel.DisplayName("当前项契约版本")] public int ContractVersion { get; init; } = 1; }
public sealed record IterationOutputOptions { [System.ComponentModel.DisplayName("空文本标记为未识别")] public bool EmptyTextIsUnrecognized { get; init; } = true; }
[SubflowSafe]
public sealed class IterationInputNode : NodeBase<IterationInputOptions>
{
    protected override IReadOnlyList<string> Validate(IterationInputOptions p) => p.ContractVersion == 1 ? [] : ["Only current-item contract version 1 is supported."];
    public override NodeDescriptor Descriptor => Describe("IterationInput", [],
        [new("Item", typeof(VisionItem)), new("Index", typeof(int)), new("Image", typeof(ImageFrame), false)]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct) => throw new InvalidOperationException("IterationInput is injected only by a ForEach run; it has no cached/default item.");
}
[SubflowSafe]
public sealed class IterationOutputNode : NodeBase<IterationOutputOptions>
{
    public override NodeDescriptor Descriptor => Describe("IterationOutput",
        [new("OCR", typeof(OcrResult), false), new("Measurement", typeof(MeasurementResult), false), new("Decision", typeof(InspectionDecision), false),
         new("Text", typeof(string), false), new("Number", typeof(double), false) { Constraint = PortValueConstraint.Numeric }, new("Confidence", typeof(double), false),
         new("IsOk", typeof(bool), false), new("ROI", typeof(RotatedRectRoi), false)], [new("Result", typeof(ItemComputation))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!c.Inputs.Keys.Any(k => k is "OCR" or "Measurement" or "Decision" or "Text" or "Number" or "IsOk")) throw new ArgumentException("Connect an explicit item result to IterationOutput.");
        var text = c.TryInput<string>("Text", out var t) ? t : "";
        double? number = c.Inputs.TryGetValue("Number", out var value) ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) : null;
        double? confidence = c.TryInput<double>("Confidence", out var conf) ? conf : null;
        var state = BusinessState.Ok; string? message = null;
        if (c.Inputs.ContainsKey("Text") && text.Length == 0 && c.Parameters<IterationOutputOptions>().EmptyTextIsUnrecognized) { state = BusinessState.Unrecognized; message = "当前项返回空文本。"; }
        var measurement = c.TryInput<MeasurementResult>("Measurement", out var m) ? m : null;
        if (measurement is { Valid: false }) { state = BusinessState.Ng; message = measurement.ErrorReason; }
        if (c.TryInput<OcrResult>("OCR", out var ocr))
        {
            if (ocr.Status is OcrStatus.MissingLanguageModel or OcrStatus.EngineError or OcrStatus.ResourceLimit)
                throw new InvalidOperationException(ocr.FailureReason ?? "OCR execution resource error.");
            text = ocr.Text; confidence = ocr.Confidence; state = ocr.Success ? BusinessState.Ok : BusinessState.Unrecognized; message = ocr.FailureReason;
        }
        if (c.TryInput<InspectionDecision>("Decision", out var d)) { state = d.IsOk ? BusinessState.Ok : BusinessState.Ng; message = d.Reason; }
        if (c.TryInput<bool>("IsOk", out var ok)) state = ok ? BusinessState.Ok : BusinessState.Ng;
        var result = new ItemComputation(text, number, confidence, state, c.TryInput<RotatedRectRoi>("ROI", out var r) ? r : null, measurement, message);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = result }));
    }
}
public sealed class ForEachNode : NodeBase<ForEachOptions>, ISubflowContainer
{
    public override NodeDescriptor Descriptor => Describe("ForEach", [new("Collection", typeof(VisionCollection)), new("Image", typeof(ImageFrame), false)],
        [new("Results", typeof(VisionCollection)), new("Batch", typeof(IterationBatch)), new("CompletedCount", typeof(int)), new("Complete", typeof(bool)), new("Debug", typeof(IterationTraceStore))]);
    public SubflowDefinition GetSubflow(NodeSpecification spec) => spec.Parameters.Deserialize<ForEachOptions>(new JsonSerializerOptions(JsonDefaults.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })?.Body ?? throw new ArgumentException("Missing subflow body.");
    protected override IReadOnlyList<string> Validate(ForEachOptions p) => p.Body is null || p.Body.Version != 1 || !Enum.IsDefined(p.FailurePolicy) ||
        p.MaximumItems is < 1 or > 10000 || p.WorkingImageMegabytes is < 1 or > 2048 || p.DebugIterations is < 0 or > 100 || p.DebugImageMegabytes is < 0 or > 512
        ? ["Invalid subflow version/limits/failure policy."] : [];
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
    {
        var p = c.Parameters<ForEachOptions>() with { Body = GetSubflow(c.Spec) }; var source = c.Input<VisionCollection>("Collection");
        if (source.Count > p.MaximumItems) throw new ArgumentException($"Collection has {source.Count} items, exceeding MaximumItems {p.MaximumItems}; explicitly filter/slice it first.");
        if (c.Services is null || !c.Services.TryGet<ISubflowRuntime>(ISubflowRuntime.ServiceKey, out var runner) || runner is null) throw new InvalidOperationException("ForEach requires RuntimeExecutor subflow services.");
        var errors = runner.Validate(p.Body); if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
        var frame = c.TryInput<ImageFrame>("Image", out var image) ? image : null;
        if (frame is null && SubflowValidation.NeedsSharedImage(p.Body)) throw new ArgumentException("ForEach.Image must supply the shared image required by IterationInput.Image; no cached frame is used.");
        var results = new List<VisionItem>(); var traces = new IterationTraceStore(p.DebugIterations, (long)p.DebugImageMegabytes * 1024 * 1024);
        string? batchError = null; var cancelled = false; var stop = false;
        try
        {
            for (var index = 0; index < source.Count; index++)
            {
                var item = source.Items[index]; var watch = Stopwatch.StartNew();
                if (ct.IsCancellationRequested) cancelled = true;
                if (stop || cancelled)
                { results.Add(new IterationItem(item.Origin, item, index, Guid.Empty, IterationStatus.NotStarted, TimeSpan.Zero, null, cancelled ? "Cancelled before this item started." : "Not run after the configured stop-on-error policy.")); continue; }
                Guid childRunId = Guid.Empty;
                try
                {
                    if (frame is not null && (item.Origin.CoordinateSpaceId != frame.CoordinateSpace.Id || item.Origin.FrameId is { } id && id != frame.FrameId))
                        throw new ArgumentException("Current item does not belong to the shared input frame/coordinate space; transform explicitly.");
                    var inputs = new Dictionary<string, object> { ["Item"] = item, ["Index"] = index };
                    ImageFrame? share = frame?.Share(); if (share is not null) inputs.Add("Image", share);
                    using var run = await runner.RunAsync(p.Body, new(inputs, share is null ? [] : [share]), c.ResourceRoot,
                        (long)p.WorkingImageMegabytes * 1024 * 1024, ct).ConfigureAwait(false);
                    childRunId = run.RunId; traces.Capture(index, run);
                    if (run.Status == NodeExecutionStatus.Cancelled) { cancelled = true; throw new OperationCanceledException(ct); }
                    if (!run.Success) throw new InvalidOperationException(run.ErrorMessage ?? "Subflow execution failed.");
                    var computation = run.Nodes[p.Body.OutputNodeId].Output<ItemComputation>("Result");
                    if (computation.Region is { } region && region.CoordinateSpaceId != item.Origin.CoordinateSpaceId ||
                        computation.Measurement is { } measurement && measurement.CoordinateSpaceId != item.Origin.CoordinateSpaceId)
                        throw new ArgumentException("Subflow result is in a different coordinate space; explicitly map local crop geometry back before collecting it.");
                    results.Add(new IterationItem(item.Origin, item, index, run.RunId, IterationStatus.Completed, watch.Elapsed, computation, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { cancelled = true; results.Add(new IterationItem(item.Origin, item, index, childRunId, IterationStatus.Cancelled, watch.Elapsed, null, "Iteration cancelled; partial batch is not complete.")); }
                catch (Exception ex)
                {
                    batchError = $"Item {index}: {ex.Message}";
                    results.Add(new IterationItem(item.Origin, item, index, childRunId, IterationStatus.Failed, watch.Elapsed, null, ex.Message));
                    stop = p.FailurePolicy == ItemFailurePolicy.Stop;
                }
            }
            var collection = new VisionCollection(VisionItemKind.Iteration, results);
            var complete = !cancelled && !stop;
            var batch = new IterationBatch(collection, complete, cancelled, batchError);
            return new(new Dictionary<string, object> { ["Results"] = collection, ["Batch"] = batch, ["CompletedCount"] = batch.CompletedCount, ["Complete"] = complete, ["Debug"] = traces }, [traces])
            {
                CompletionStatus = cancelled ? NodeExecutionStatus.Cancelled : stop ? NodeExecutionStatus.Failed : NodeExecutionStatus.Success,
                CompletionError = cancelled ? "ForEach cancelled; inspect completed and unfinished items." : stop ? batchError : null
            };
        }
        catch { traces.Dispose(); throw; }
    }
}

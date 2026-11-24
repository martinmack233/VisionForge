using System.Text.Json.Nodes;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Contracts.Recognition;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class CollectionFlowTests
{
    private static ItemOrigin Origin(int i) => new(Guid.NewGuid(), "source", i, null, "image", "association-" + i);
    private static VisionCollection Rois(int count) => new(VisionItemKind.Roi, Enumerable.Range(0, count).Select(i => new RoiItem(Origin(i), new(20 + i * 30, 20, 20 + i * 10, 10))));
    private static async Task<RunResult> Single(string type, object parameters, string port, object input) => await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("test", type, parameters), new Dictionary<string, object> { [port] = input });
    [Fact]
    public async Task FilterStableSortSliceAndZeroBasedSelectionRetainOrigins()
    {
        var c = new VisionCollection(VisionItemKind.Detection, new[] { ("a", 20d), ("b", 10d), ("c", 20d) }.Select((v, i) => new DetectionItem(Origin(i), new(i, v.Item1, .8, new(v.Item2, 0, 10, 10)))));
        using var sort = await Single("CollectionSort", new { Field = "X" }, "Collection", c);
        var sorted = sort.Nodes["test"].Output<VisionCollection>("Collection"); Assert.Equal(new[] { 1, 0, 2 }, sorted.Items.Select(i => i.Origin.SourceIndex));
        using var filter = await Single("CollectionFilter", new CollectionFilterOptions { Field = "Confidence", Number = .7 }, "Collection", sorted);
        Assert.Equal(sorted.Items.Select(i => i.Origin.Key), filter.Nodes["test"].Output<VisionCollection>("Collection").Items.Select(i => i.Origin.Key));
        using var selected = await Single("CollectionSelect", new { Index = 0 }, "Collection", sorted); Assert.Same(sorted.Items[0], selected.Nodes["test"].Output<VisionItem>("Item"));
        using var bad = await Single("CollectionSelect", new { Index = 3 }, "Collection", sorted); Assert.False(bad.Success); Assert.Contains("Zero-based", bad.ErrorMessage);
        using var slice = await Single("CollectionSlice", new { Mode = "Range", Start = 1, Count = 2 }, "Collection", sorted); Assert.Equal(2, slice.Nodes["test"].Output<VisionCollection>("Collection").Count);
        using var unsupported = await Single("CollectionFilter", new { Field = "FakeMetric" }, "Collection", sorted); Assert.False(unsupported.Success);
    }
    [Fact]
    public async Task MissingBlobMetricIsNotInventedAndSnapshotsAreReadOnly()
    {
        var runs = new List<BlobPixelRun> { new(1, 2, 3) }; var b = new BlobResult(1, 3, 2, 2, new(1, 1, 3, 1)) { PixelRuns = runs };
        var c = new VisionCollection(VisionItemKind.Blob, [new BlobItem(Origin(0), b)]); runs.Clear();
        Assert.Single(((BlobItem)c.Items[0]).Blob.PixelRuns!); Assert.Throws<NotSupportedException>(() => ((IList<VisionItem>)c.Items).Clear());
        using var run = await Single("CollectionFilter", new { Field = "Circularity" }, "Collection", c); Assert.False(run.Success); Assert.Contains("not actually measured", run.ErrorMessage);
    }
    [Fact]
    public void TransformsAreExplicitAndRejectShearUnlessEnvelopeChosen()
    {
        var item = new RoiItem(Origin(0), new(10, 20, 12, 8, 30));
        Assert.Throws<ArgumentException>(() => ItemRoiConversion.Convert(item, new(1, 0, 0, 1, 0, 0, "local", "image"), false));
        Assert.Throws<ArgumentException>(() => ItemRoiConversion.Convert(item, new(1, .4, 0, 1, 0, 0, "image", "target"), false));
        var roi = ItemRoiConversion.Convert(item, new(1, .4, 0, 1, 0, 0, "image", "target"), true); Assert.Equal("target", roi.CoordinateSpaceId); Assert.Equal(0, roi.AngleDegrees);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task FiniteSubflowHandlesEmptySingleAndMultipleWithoutOldValues(int count)
    {
        using var run = await Single("ForEach", new ForEachOptions { Body = CollectionSubflows.Measure() }, "Collection", Rois(count));
        Assert.True(run.Success, run.ErrorMessage); var batch = run.Nodes["test"].Output<IterationBatch>("Batch"); Assert.True(batch.Complete); Assert.Equal(count, batch.CompletedCount);
        Assert.Equal(count, batch.Results.Count); Assert.Equal(count, batch.Results.Items.Cast<IterationItem>().Select(i => i.IterationRunId).Distinct().Count());
        foreach (var item in batch.Results.Items.Cast<IterationItem>()) Assert.Equal((20 + item.Index * 10) / 2d, item.Result!.Number);
    }
    [Fact]
    public async Task ConvertedRoisPreserveActualDetectionAndRequireExplicitTargetFrame()
    {
        using var target = new ImageFrameBuilder(100, 100, PixelFormat.Mono8).Build();
        var origin = Origin(2) with { CoordinateSpaceId = "crop", FrameId = Guid.NewGuid() };
        var detection = new DetectionItem(origin, new(4, "part", .987654321, new(1, 2, 10, 12)));
        var transform = new CoordinateTransform2D(1, 0, 0, 1, 20, 30, "crop", target.CoordinateSpace.Id);
        var node = CollectionSubflows.N("convert", "ItemToRoi", new { });
        var inputs = new Dictionary<string, object> { ["Item"] = detection, ["Transform"] = transform };
        using (var missing = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(node, inputs))
        { Assert.False(missing.Success); Assert.Contains("TargetImage", missing.ErrorMessage); }
        inputs["TargetImage"] = target;
        using var converted = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(node, inputs);
        Assert.True(converted.Success, converted.ErrorMessage); var item = Assert.IsType<RoiItem>(converted.Nodes["convert"].Output<VisionItem>("Item"));
        Assert.Equal(target.FrameId, item.Origin.FrameId); Assert.Equal(origin.Key, item.Origin.Key); Assert.Equal(26, item.Roi.CenterX); Assert.Equal(38, item.Roi.CenterY);
        Assert.Equal(origin.FrameId, item.OriginalItem!.Origin.FrameId); Assert.Equal(.987654321, item.Fields["Confidence"].Number);
    }
    [Theory]
    [InlineData(ItemFailurePolicy.Stop, 1, false)] [InlineData(ItemFailurePolicy.Continue, 2, true)]
    public async Task ExplicitFailurePolicyRetainsFailureAndUnfinished(ItemFailurePolicy policy, int completed, bool complete)
    {
        var registry = BuiltinRegistration.CreateRegistry(); registry.Register("Probe", () => new ProbeNode());
        using var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(CollectionSubflows.N("each", "ForEach", new ForEachOptions { Body = ProbeBody(), FailurePolicy = policy }), new Dictionary<string, object> { ["Collection"] = Rois(3) });
        var batch = run.Nodes["each"].Output<IterationBatch>("Batch"); Assert.Equal(completed, batch.CompletedCount); Assert.Equal(complete, batch.Complete);
        Assert.Equal(IterationStatus.Failed, ((IterationItem)batch.Results.Items[1]).Status); Assert.Equal(BusinessState.Error, batch.Results.Items[1].State);
        if (!complete) Assert.Equal(IterationStatus.NotStarted, ((IterationItem)batch.Results.Items[2]).Status);
    }
    [Fact]
    public async Task CancellationRetainsCompletedItemsAndDoesNotClaimComplete()
    {
        using var ct = new CancellationTokenSource(); var registry = BuiltinRegistration.CreateRegistry(); registry.Register("Probe", () => new ProbeNode(ct));
        using var run = await new RuntimeExecutor(registry).ExecuteSingleAsync(CollectionSubflows.N("each", "ForEach", new ForEachOptions { Body = ProbeBody() }), new Dictionary<string, object> { ["Collection"] = Rois(3) }, cancellationToken: ct.Token);
        Assert.Equal(NodeExecutionStatus.Cancelled, run.Status); var batch = run.Nodes["each"].Output<IterationBatch>("Batch"); Assert.True(batch.Cancelled); Assert.False(batch.Complete); Assert.Equal(1, batch.CompletedCount);
        Assert.Equal(new[] { IterationStatus.Completed, IterationStatus.Cancelled, IterationStatus.NotStarted }, batch.Results.Items.Cast<IterationItem>().Select(i => i.Status));
    }
    [Theory]
    [InlineData(OcrStatus.NoText, true)] [InlineData(OcrStatus.LowConfidence, true)]
    [InlineData(OcrStatus.MissingLanguageModel, false)] [InlineData(OcrStatus.EngineError, false)]
    public async Task OcrBusinessRejectionAndExecutionErrorsRemainDistinct(OcrStatus status, bool executes)
    {
        var ocr = new OcrResult(false, status == OcrStatus.LowConfidence ? "uncertain" : "", .1, [], "image", TimeSpan.Zero, [], status.ToString()) { Status = status };
        using var run = await Single("IterationOutput", new { }, "OCR", ocr);
        Assert.Equal(executes, run.Success);
        if (executes) Assert.Equal(BusinessState.Unrecognized, run.Nodes["test"].Output<ItemComputation>("Result").State);
        else Assert.Contains(status.ToString(), run.ErrorMessage);
    }
    [Fact]
    public async Task OcrCollectionAdapterDoesNotTurnLowConfidenceIntoOkOrEngineFailureIntoEmptySuccess()
    {
        var low = new OcrResult(false, "uncertain", .1, [new("uncertain", .1, new(0, 0, 10, 10))], "image", TimeSpan.Zero, [], "low confidence") { Status = OcrStatus.LowConfidence };
        using var run = await Single("CollectionAdapter", new { }, "OCR", low);
        Assert.True(run.Success); Assert.Equal(BusinessState.Unrecognized, run.Nodes["test"].Output<VisionCollection>("Collection").Items.Single().State);
        using var missing = await Single("CollectionAdapter", new { }, "OCR", low with { Status = OcrStatus.MissingLanguageModel, Words = [], FailureReason = "language missing" });
        Assert.False(missing.Success); Assert.Contains("language missing", missing.ErrorMessage);
    }
    [Fact]
    public void SideEffectsNestedGraphsMissingRoiAndUnknownFieldsAreRejected()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var body = CollectionSubflows.Ocr();
        Assert.Contains(SubflowValidation.Validate(registry, body with { Nodes = body.Nodes.Append(CollectionSubflows.N("send", "TcpSend", new { })).ToList() }), e => e.Contains("forbidden"));
        Assert.Contains(SubflowValidation.Validate(registry, body with { Nodes = body.Nodes.Append(CollectionSubflows.N("nested", "ForEach", new ForEachOptions { Body = CollectionSubflows.Measure() })).ToList() }), e => e.Contains("Nested"));
        Assert.Contains(SubflowValidation.Validate(registry, body with { Connections = body.Connections.Where(e => e.TargetPort != "ROI").ToList() }), e => e.Contains("fall back"));
        var json = JsonNode.Parse(JsonDefaults.Element(new ForEachOptions { Body = body }).GetRawText())!; json["Body"]!["Unknown"] = 1;
        Assert.NotEmpty(new ForEachNode().ValidateParameters(System.Text.Json.JsonSerializer.SerializeToElement(json)));
    }
    [Fact]
    public async Task WorkingAndDebugImageBudgetsReleaseShares()
    {
        using var image = new ImageFrameBuilder(1600, 1000, PixelFormat.Mono8).Build(); var baselineRefs = image.BufferHandle.ReferenceCount;
        for (var i = 0; i < 5; i++)
        {
            using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("each", "ForEach", new ForEachOptions { Body = CollectionSubflows.Measure(), DebugIterations = 1, DebugImageMegabytes = 1 }),
                new Dictionary<string, object> { ["Collection"] = Rois(3), ["Image"] = image });
            Assert.True(run.Success, run.ErrorMessage); var trace = run.Nodes["each"].Output<IterationTraceStore>("Debug"); Assert.Single(trace.Traces); Assert.Equal(0, trace.RetainedBytes);
        }
        Assert.Equal(baselineRefs, image.BufferHandle.ReferenceCount);
        using var limited = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(CollectionSubflows.N("each", "ForEach", new ForEachOptions { Body = CollectionSubflows.Measure(), WorkingImageMegabytes = 1 }), new Dictionary<string, object> { ["Collection"] = Rois(1), ["Image"] = image });
        Assert.False(limited.Success); Assert.Contains("budget", limited.ErrorMessage);
    }
    [Fact]
    public async Task RealOcrAndBlobExamplesRunAndReloadThroughSameRuntime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionCollections-" + Guid.NewGuid().ToString("N")); await CollectionExampleFactory.SaveAllAsync(directory);
        var registry = BuiltinRegistration.CreateRegistry(); var store = new ProjectStore(registry);
        foreach (var ocr in new[] { true, false })
        {
            var folder = Path.Combine(directory, ocr ? "collection-multi-ocr" : "collection-blob-measure"); var path = Path.Combine(folder, "project.json");
            var project = await store.LoadAsync(path); using var run = await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), folder);
            Assert.True(run.Success, run.ErrorMessage); var batch = run.Nodes["each"].Output<IterationBatch>("Batch"); var items = batch.Results.Items.Cast<IterationItem>().ToArray(); Assert.Equal(3, items.Length);
            if (ocr) Assert.Equal(new[] { "VISION", "123456", "PART42" }, items.Select(i => i.Result!.Text));
            else { Assert.Equal(new double?[] { 10, 15, 25 }, items.Select(i => i.Result!.Number)); Assert.Equal(new[] { BusinessState.Ng, BusinessState.Ok, BusinessState.Ok }, items.Select(i => i.State)); }
            Assert.All(items, i => Assert.Equal(i.Origin, i.SourceItem.Origin));
            var sourceItems = run.Nodes[ocr ? "regions" : "sort"].Output<VisionCollection>("Collection").Items;
            Assert.Equal(sourceItems.Select(i => i.Origin.Key), items.Select(i => i.Origin.Key));
            await store.SaveAsync(Path.Combine(folder, "roundtrip.json"), project); var reopened = await store.LoadAsync(Path.Combine(folder, "roundtrip.json")); Assert.Equal(project.Nodes[ocr ? 2 : 5].Parameters.GetRawText(), reopened.Nodes[ocr ? 2 : 5].Parameters.GetRawText());
        }
    }
    private static SubflowDefinition ProbeBody() => new() { Nodes = [CollectionSubflows.N("current", "IterationInput", new { }), CollectionSubflows.N("probe", "Probe", new { }), CollectionSubflows.N("collect", "IterationOutput", new { })],
        Connections = [new("current", "Index", "probe", "Index"), new("probe", "Text", "collect", "Text")] };
    [SubflowSafe]
    private sealed class ProbeNode(CancellationTokenSource? cancellation = null) : NodeBase<EmptyCollectionOptions>
    {
        public override NodeDescriptor Descriptor => Describe("Probe", [new("Index", typeof(int))], [new("Text", typeof(string))]);
        public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c, CancellationToken ct)
        { var index = c.Input<int>("Index"); if (index == 1) { if (cancellation is not null) { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); } throw new InvalidOperationException("intentional item execution error"); } return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Text"] = "item-" + index })); }
    }
}

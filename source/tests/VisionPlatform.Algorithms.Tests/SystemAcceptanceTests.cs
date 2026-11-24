using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Algorithms.Blob;
using VisionPlatform.Algorithms.ImageProcessing;
using VisionPlatform.Contracts.Matching;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class SystemAcceptanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-Acceptance-" + Guid.NewGuid().ToString("N"));
    public SystemAcceptanceTests() => DemoProjectFactory.CreateResources(_directory);

    [Theory]
    [InlineData(false, "sample.png", 2, true)]
    [InlineData(true, "sample-color.png", 2, true)]
    [InlineData(false, "shifted.png", 2, true)]
    [InlineData(true, "shifted.png", 2, true)]
    [InlineData(false, "ng.png", 3, false)]
    public async Task ConsoleFixturesProduceMeasuredDecisions(bool preprocess, string image, int count, bool accepted)
    {
        var document = DemoProjectFactory.Create(preprocess);
        var nodes = document.Nodes.Select(n => n.NodeId == "input" ? n with
        { Parameters = JsonDefaults.Element(new ImageInputParameters { Path = image }) } : n);
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(new NodeGraph(nodes, document.Connections), _directory);
        Assert.True(run.Success, run.ErrorMessage);
        Assert.True(run.Nodes["match"].Output<TemplateMatchResult>("MatchResult").Success);
        Assert.Equal(count, run.Nodes["blob"].Output<int>("BlobCount"));
        Assert.Equal(accepted, run.Nodes["decision"].Output<bool>("IsOk"));
    }

    [Fact]
    public async Task RepeatedCompleteRunsAndFailingRunsReleaseEveryPlatformBuffer()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        var graph = DemoProjectFactory.Create(true).ToGraph();
        var initialBytes = FrameHandle.ActiveBytes; var initialAllocations = FrameHandle.ActiveAllocations;
        for (var i = 0; i < 20; i++)
        {
            using (var run = await executor.ExecuteAsync(graph, _directory)) Assert.True(run.Success, run.ErrorMessage);
            Assert.Equal(initialBytes, FrameHandle.ActiveBytes);
            Assert.Equal(initialAllocations, FrameHandle.ActiveAllocations);
        }
        var missingTemplate = graph.Nodes.Select(n => n.NodeId == "match" ? n with
        { Parameters = JsonDefaults.Element(new TemplateMatchingParameters { TemplatePath = "missing.json" }) } : n);
        using (var run = await executor.ExecuteAsync(new NodeGraph(missingTemplate, graph.Connections), _directory))
        {
            Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["match"].Status);
            Assert.Contains("missing", run.Nodes["match"].ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(initialBytes, FrameHandle.ActiveBytes);
        Assert.Equal(initialAllocations, FrameHandle.ActiveAllocations);
    }

    [Fact]
    public void LargeMonoFrameFiltersAndDetectsOnlyItsRoi()
    {
        var before = FrameHandle.ActiveBytes;
        using (var builder = new ImageFrameBuilder(8192, 10000, VisionPlatform.Contracts.Images.PixelFormat.Mono8))
        {
            for (var y = 4500; y < 4509; y++) builder.GetRowSpan(y).Slice(4000, 9).Fill(245);
            using var image = builder.Build();
            using var filtered = GaussianBlurProcessor.Process(image, new(), new RectRoi(3992, 4492, 32, 32));
            Assert.Equal(32, filtered.Width); Assert.Equal(32, filtered.Height);
            Assert.Equal(3992, filtered.CoordinateSpace.OriginX);
            Assert.Equal(before + 8192L * 10000 + 32 * 32, FrameHandle.ActiveBytes);
            var blobs = BlobDetector.Detect(image, new RotatedRectRoi(4004.5, 4504.5, 30, 30), new() { MinArea = 3 });
            var blob = Assert.Single(blobs.Items);
            Assert.Equal(81, blob.Area); Assert.Equal(4004, blob.CenterX); Assert.Equal(4504, blob.CenterY);
            using var pixels = filtered.Lease();
            Assert.InRange(pixels.GetRowSpan(12)[12], (byte)230, (byte)245);
        }
        Assert.Equal(before, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task InvalidMatcherOptionsFailParameterValidation()
    {
        var spec = DemoProjectFactory.Node("match", "TemplateMatching", new TemplateMatchingParameters
        { Options = new TemplateMatchOptions { ScaleMin = -.5 } });
        using var input = VisionPlatform.Imaging.ImageFile.Load(Path.Combine(_directory, "sample.png"));
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(spec,
            new Dictionary<string, object> { ["Image"] = input }, _directory);
        Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["match"].Status);
        Assert.Contains("scale", run.Nodes["match"].ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(input.IsDisposed);
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_directory);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("VisionPlatform-Acceptance-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected acceptance fixture directory.");
        Directory.Delete(path, true);
    }
}

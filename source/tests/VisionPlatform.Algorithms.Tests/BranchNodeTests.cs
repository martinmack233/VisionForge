using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class BranchNodeTests
{
    private static NodeSpecification Spec(string type, object options) => new("node", type, type, JsonDefaults.Element(options));

    [Theory]
    [InlineData(true, false, "True")]
    [InlineData(false, false, "False")]
    [InlineData(true, true, "False")]
    [InlineData(false, true, "True")]
    public async Task IfElseProducesOnlyTheChosenControlOutput(bool condition, bool invert, string expected)
    {
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(
            Spec("IfElse", new IfElseParameters { InvertCondition = invert }), new Dictionary<string, object> { ["Condition"] = condition });
        Assert.True(run.Success); var output = Assert.Single(run.Nodes["node"].Outputs);
        Assert.Equal(expected, output.Key); Assert.True(Assert.IsType<ControlSignal>(output.Value).IsActive);
    }

    [Theory]
    [InlineData("current", false, "current")]
    [InlineData("current", true, "True: current")]
    public async Task MergePublishesRawSelectedTextAndOptionalBranchPrefix(string text, bool prefix, string expected)
    {
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(Spec("Merge", new MergeParameters { IncludeBranchNameInText = prefix }),
            new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Active, ["TrueValue"] = text, ["FalseControl"] = ControlSignal.Inactive });
        var node = run.Nodes["node"]; Assert.True(run.Success); Assert.Equal(text, node.Output<string>("Value"));
        Assert.Equal(expected, node.Output<string>("Text")); Assert.Equal("True", node.Output<string>("ActiveBranch"));
    }

    [Fact]
    public async Task MergeImageOwnsOnlyAShareAndNeverDisposesBorrowedInput()
    {
        var baseline = FrameHandle.ActiveBytes;
        using (var input = new ImageFrameBuilder(12, 10, PixelFormat.Mono8).Build())
        {
            var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(Spec("Merge", new MergeParameters()),
                new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Inactive, ["FalseControl"] = ControlSignal.Active, ["FalseValue"] = input });
            var shared = run.Nodes["node"].Output<ImageFrame>("Image"); Assert.NotSame(input, shared); Assert.Same(input.BufferHandle, shared.BufferHandle);
            Assert.Same(shared, run.Nodes["node"].Output<ImageFrame>("Value"));
            run.Dispose(); Assert.True(shared.IsDisposed); Assert.False(input.IsDisposed);
            using var lease = input.Lease(); Assert.Equal(12, lease.GetRowSpan(0).Length);
        }
        Assert.Equal(baseline, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task MergePublishesStructuredReportAndBooleanWithoutConvertingThemToStrings()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        var report = new InspectionReport { IsOk = true, BlobCount = 2 };
        using var first = await executor.ExecuteSingleAsync(Spec("Merge", new MergeParameters()),
            new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Active, ["TrueValue"] = report });
        Assert.Same(report, first.Nodes["node"].Output<InspectionReport>("Result")); Assert.False(first.Nodes["node"].Outputs.ContainsKey("Text"));
        using var second = await executor.ExecuteSingleAsync(Spec("Merge", new MergeParameters()),
            new Dictionary<string, object> { ["FalseControl"] = ControlSignal.Active, ["FalseValue"] = false });
        Assert.False(second.Nodes["node"].Output<bool>("Boolean")); Assert.Equal("False", second.Nodes["node"].Output<string>("ActiveBranch"));
    }

    [Fact]
    public async Task MergePreservesOriginalIntegerWhileProvidingAnExplicitDoublePort()
    {
        const long value = 9007199254740993L;
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(Spec("Merge", new MergeParameters()),
            new Dictionary<string, object> { ["TrueControl"] = ControlSignal.Active, ["TrueValue"] = value });
        Assert.Equal(value, run.Nodes["node"].Output<long>("Value")); Assert.IsType<double>(run.Nodes["node"].Outputs["Number"]);
    }

    [Fact]
    public async Task MergePublishesCommunicationStatusThroughItsStronglyTypedPort()
    {
        var status = new CommunicationStatus(CommunicationState.Sent, true, "Sent", 4);
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(Spec("Merge", new MergeParameters()),
            new Dictionary<string, object> { ["FalseControl"] = ControlSignal.Active, ["FalseValue"] = status });
        Assert.Same(status, run.Nodes["node"].Output<CommunicationStatus>("Communication"));
        Assert.Equal("False", run.Nodes["node"].Output<string>("ActiveBranch"));
    }

    [Fact]
    public async Task MergeRejectsConcurrentActiveBranchesAndCancellationHasNoOutputs()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        using var invalid = await executor.ExecuteSingleAsync(Spec("Merge", new MergeParameters()), new Dictionary<string, object>
        { ["TrueControl"] = ControlSignal.Active, ["TrueValue"] = "a", ["FalseControl"] = ControlSignal.Active, ["FalseValue"] = "b" });
        Assert.Equal(NodeExecutionStatus.Failed, invalid.Status); Assert.Empty(invalid.Nodes["node"].Outputs);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var cancelled = await executor.ExecuteSingleAsync(Spec("Merge", new MergeParameters()), new Dictionary<string, object>
        { ["TrueControl"] = ControlSignal.Inactive, ["FalseControl"] = ControlSignal.Inactive }, cancellationToken: cancellation.Token);
        Assert.Equal(NodeExecutionStatus.Cancelled, cancelled.Status); Assert.Empty(cancelled.Nodes["node"].Outputs);
    }
}

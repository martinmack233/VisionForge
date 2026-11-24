using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;

public sealed class Phase3BLogicTests
{
    [Theory]
    [InlineData(CompareOperator.GreaterThan, true)]
    [InlineData(CompareOperator.LessThan, false)]
    [InlineData(CompareOperator.Equal, false)]
    [InlineData(CompareOperator.NotEqual, true)]
    [InlineData(CompareOperator.GreaterOrEqual, true)]
    [InlineData(CompareOperator.LessOrEqual, false)]
    public void SixOperatorsUseNumericOrdering(CompareOperator operation, bool expected)
        => Assert.Equal(expected, NumericComparer.Compare(4, 2.5, operation, 0));

    [Theory]
    [InlineData(CompareOperator.GreaterThan, false)]
    [InlineData(CompareOperator.LessThan, false)]
    [InlineData(CompareOperator.Equal, true)]
    [InlineData(CompareOperator.NotEqual, false)]
    [InlineData(CompareOperator.GreaterOrEqual, true)]
    [InlineData(CompareOperator.LessOrEqual, true)]
    public void AbsoluteToleranceDefinesEqualityBandForEveryOperator(CompareOperator operation, bool expected)
        => Assert.Equal(expected, NumericComparer.Compare(2.125, 2, operation, 0.125));

    [Fact]
    public void WideIntegersRetainPrecisionAndSigns()
    {
        Assert.True(NumericComparer.Compare(long.MaxValue, long.MaxValue - 1, CompareOperator.GreaterThan, 0));
        Assert.False(NumericComparer.Compare(long.MaxValue, long.MaxValue - 1, CompareOperator.Equal, 0));
        Assert.True(NumericComparer.Compare(ulong.MaxValue, long.MaxValue, CompareOperator.GreaterThan, 0));
        Assert.True(NumericComparer.Compare(long.MinValue, long.MaxValue, CompareOperator.LessThan, 0));
        Assert.True(NumericComparer.Compare(9007199254740993L, 9007199254740992d, CompareOperator.GreaterThan, 0));
        Assert.True(NumericComparer.Compare(long.MaxValue, (double)long.MaxValue, CompareOperator.LessThan, 0));
    }

    [Fact]
    public void DecimalAndBinaryPrecisionUseTheirActualValuesWithoutOverflow()
    {
        Assert.False(NumericComparer.Compare(.1m, .1d, CompareOperator.Equal, 0));
        Assert.True(NumericComparer.Compare(.1m, .1d, CompareOperator.Equal, 1e-12));
        Assert.True(NumericComparer.Compare(decimal.MaxValue, double.MaxValue, CompareOperator.LessThan, 0));
        Assert.True(NumericComparer.Compare(double.Epsilon, 0, CompareOperator.GreaterThan, 0));
        Assert.True(NumericComparer.Compare(-double.MaxValue, decimal.MinValue, CompareOperator.LessThan, 0));
        Assert.True(NumericComparer.Compare(-0d, 0m, CompareOperator.Equal, 0));
        Assert.True(NumericComparer.Compare(1f, (byte)1, CompareOperator.Equal, 0));
    }

    [Fact]
    public void InvalidValuesAndOptionsAreExplicitlyRejected()
    {
        foreach (var invalid in new object[] { "2", true, double.NaN, double.PositiveInfinity, float.NegativeInfinity, new object() })
            Assert.Throws<ArgumentException>(() => NumericComparer.Compare(invalid, 2, CompareOperator.Equal));
        Assert.Throws<ArgumentException>(() => NumericComparer.Compare(null!, 2, CompareOperator.Equal));
        Assert.Throws<ArgumentException>(() => NumericComparer.Compare(1, 1, (CompareOperator)999));
        foreach (var invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentException>(() => NumericComparer.Compare(1, 1, CompareOperator.Equal, invalid));
            Assert.NotEmpty(NumericComparer.Validate(new() { Tolerance = invalid }));
        }
        Assert.NotEmpty(NumericComparer.Validate(new() { Operator = (CompareOperator)999 }));
        Assert.NotEmpty(NumericComparer.Validate(new() { ValueB = double.NaN }));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public void BooleanTruthTables(bool a, bool b, bool and, bool or)
    {
        Assert.Equal(and, BooleanLogic.And(a, b)); Assert.Equal(or, BooleanLogic.Or(a, b));
        Assert.Equal(!a, BooleanLogic.Not(a));
    }

    [Fact]
    public async Task RegisteredNodesUseTypedPortsAndConstantFallbacks()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        using (var run = await executor.ExecuteSingleAsync(Node("Compare", new CompareOptions { ValueB = 3 }), new Dictionary<string, object> { ["ValueA"] = 5 }))
            Assert.True(run.Nodes["n"].Output<bool>("Result"));
        using (var run = await executor.ExecuteSingleAsync(Node("Compare", new CompareOptions { ValueB = 3, Operator = CompareOperator.LessThan }),
            new Dictionary<string, object> { ["ValueA"] = long.MaxValue, ["ValueB"] = long.MaxValue - 1, ["Operator"] = CompareOperator.GreaterThan }))
            Assert.True(run.Nodes["n"].Output<bool>("Result"));
        foreach (var type in new[] { "LogicAnd", "LogicOr" })
        {
            using var run = await executor.ExecuteSingleAsync(Node(type, new LogicBinaryOptions { ValueA = true, ValueB = true }), new Dictionary<string, object>());
            Assert.True(run.Nodes["n"].Output<bool>("Result"));
        }
        using (var run = await executor.ExecuteSingleAsync(Node("LogicNot", new LogicNotOptions { Value = true }), new Dictionary<string, object> { ["Value"] = false }))
            Assert.True(run.Nodes["n"].Output<bool>("Result"));
        var compare = new CompareNode().Descriptor;
        Assert.Equal(PortValueConstraint.Numeric, compare.InputPorts.Single(p => p.Name == "ValueA").Constraint);
        Assert.All(new LogicAndNode().Descriptor.InputPorts, p => Assert.Equal(typeof(bool), p.DataType));
    }

    [Fact]
    public async Task RuntimeReportsNonfiniteAndInvalidParametersWithoutCrashing()
    {
        var executor = new RuntimeExecutor(BuiltinRegistration.CreateRegistry());
        using var run = await executor.ExecuteSingleAsync(Node("Compare", new CompareOptions()), new Dictionary<string, object> { ["ValueA"] = double.NaN });
        Assert.False(run.Success); Assert.Equal(NodeExecutionStatus.Failed, run.Nodes["n"].Status);
        Assert.Contains("finite", run.Nodes["n"].ErrorMessage);
        using var invalid = await executor.ExecuteSingleAsync(Node("Compare", new CompareOptions { Tolerance = -1 }), new Dictionary<string, object> { ["ValueA"] = 2 });
        Assert.False(invalid.Success); Assert.Contains("Tolerance", invalid.Nodes["n"].ErrorMessage);
    }

    private static NodeSpecification Node<T>(string type, T options) => new("n", type, type, JsonDefaults.Element(options));
}

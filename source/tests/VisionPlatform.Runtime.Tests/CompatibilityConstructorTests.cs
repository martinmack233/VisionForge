using Microsoft.Extensions.Logging;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class CompatibilityConstructorTests
{
    [Fact]
    public void OriginalPublishedConstructorSignaturesRemainAvailable()
    {
        Assert.NotNull(typeof(NodeExecutionContext).GetConstructor([typeof(NodeSpecification), typeof(IReadOnlyDictionary<string, object>), typeof(string)]));
        Assert.NotNull(typeof(RuntimeExecutor).GetConstructor([typeof(NodeRegistry), typeof(ILogger)]));
    }
}

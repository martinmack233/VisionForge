using System.Text.Json;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Hosting.Fixture;

/// <summary>Trusted, deterministic plugin fixture for actual managed assembly discovery tests.</summary>
public sealed class FixtureEchoNode : IExecutableNode
{
    public NodeDescriptor Descriptor => new("HostingFixtureEcho", "1.0.0", [], [new("Number", typeof(double))]);
    public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => [];
    public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Number"] = 42d })); }
}

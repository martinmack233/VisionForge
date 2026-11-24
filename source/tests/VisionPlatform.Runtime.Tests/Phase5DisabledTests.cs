using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class Phase5DisabledTests
{
    private int _calls;
    private NodeRegistry Registry()
    {
        var registry = new NodeRegistry();
        registry.Register("source", () => new FixtureNode("source", [], () => _calls++));
        registry.Register("consumer", () => new FixtureNode("consumer", [new("Value", typeof(int))], () => _calls++));
        return registry;
    }
    private static NodeSpecification Spec(string id, string type, bool enabled = true) => new(id, type, id, JsonDefaults.Element(new { })) { IsEnabled = enabled };

    [Fact]
    public async Task DisabledRequiredInputsNeverBlockAnIndependentFlowOrInvokeSideEffects()
    {
        var registry = Registry();
        var graph = new NodeGraph([Spec("off", "consumer", false), Spec("live", "source")], []);
        Assert.Empty(new GraphValidator(registry).Validate(graph));
        using var result = await new RuntimeExecutor(registry).ExecuteAsync(graph);
        Assert.True(result.Success); Assert.Equal(1, _calls);
        Assert.Equal(NodeSkipReason.Disabled, result.Nodes["off"].SkipReason); Assert.Empty(result.Nodes["off"].Outputs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeExecutor(registry).ExecuteSingleAsync(graph.Nodes[0], new Dictionary<string, object>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeExecutor(registry).ExecuteToAsync(graph, "off"));
    }

    [Theory]
    [InlineData("Value", "Value")]
    [InlineData("$done", "$exec")]
    public void DisabledConnectionsAreRejectedForBothPortKinds(string output, string input)
    {
        var errors = new GraphValidator(Registry()).Validate(new([Spec("off", "source", false), Spec("on", "consumer")], [new("off", output, "on", input)]));
        Assert.Contains(errors, e => e.Contains("Disabled"));
    }

    [Fact]
    public async Task Schema2PersistsDisabledDraftAndRuntimeReportsDisconnectedDownstream()
    {
        var registry = Registry(); var store = new ProjectStore(registry);
        var root = Path.Combine(Path.GetTempPath(), "VisionPlatform-Phase5-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "project.json");
            await store.SaveAsync(path, new() { Nodes = [Spec("off", "source", false), Spec("needs", "consumer")] });
            var loaded = await store.LoadAsync(path);
            Assert.Equal(ProjectDocument.CurrentSchemaVersion, loaded.SchemaVersion); Assert.False(loaded.Nodes[0].IsEnabled);
            var error = await Assert.ThrowsAsync<GraphValidationException>(() => new RuntimeExecutor(registry).ExecuteAsync(loaded.ToGraph()));
            Assert.Contains("needs.Value", error.Message); Assert.Equal(0, _calls);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ProjectDocument { SchemaVersion = 1, Nodes = [Spec("old", "source")] }, JsonDefaults.Options));
            var migrated = await store.LoadAsync(path); Assert.Equal(ProjectDocument.CurrentSchemaVersion, migrated.SchemaVersion); Assert.True(migrated.Nodes[0].IsEnabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitCurrentRunSnapshotDoesNotExecuteSourceAndAlwaysReleasesOwnership()
    {
        var registry = Registry(); var executor = new RuntimeExecutor(registry); var resource = new Probe();
        var graph = new NodeGraph([Spec("source", "source"), Spec("next", "consumer")], [new("source", "Value", "next", "Value")]);
        using (var run = await executor.ExecuteWithSourceAsync(graph, "source", new(new Dictionary<string, object> { ["Value"] = 17 }, [resource])))
        { Assert.True(run.Success); Assert.Equal(1, _calls); Assert.Equal(17, run.Nodes["source"].Output<int>("Value")); Assert.Equal(0, resource.Count); }
        Assert.Equal(1, resource.Count);
        var invalidResource = new Probe();
        await Assert.ThrowsAsync<GraphValidationException>(() => executor.ExecuteWithSourceAsync(graph, "source", new(new Dictionary<string, object> { ["Value"] = "wrong" }, [invalidResource])));
        Assert.Equal(1, invalidResource.Count);
    }
    [Fact]
    public async Task IntermediateSchema2MigratesAndStillRejectsUnknownFields()
    {
        var store = new ProjectStore(Registry());
        var directory = Path.Combine(Path.GetTempPath(), "VisionPlatform-Phase5-Schema-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "schema2.json");
            var document = new ProjectDocument { SchemaVersion = 2, Nodes = [Spec("off", "consumer", false), Spec("live", "source")] };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonDefaults.Options));
            var migrated = await store.LoadAsync(path);
            Assert.Equal(ProjectDocument.CurrentSchemaVersion, migrated.SchemaVersion); Assert.False(migrated.Nodes[0].IsEnabled); Assert.Null(migrated.AiProject);
            using (var run = await new RuntimeExecutor(Registry()).ExecuteAsync(migrated.ToGraph())) Assert.True(run.Success);
            await store.SaveAsync(path, migrated); Assert.Equal(ProjectDocument.CurrentSchemaVersion, (await store.LoadAsync(path)).SchemaVersion);
            var unknown = JsonNode.Parse(await File.ReadAllTextAsync(path))!; unknown["UnknownFutureField"] = true;
            await File.WriteAllTextAsync(path, unknown.ToJsonString()); await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Probe : IDisposable { public int Count; public void Dispose() => Count++; }
    private sealed class FixtureNode(string type, IReadOnlyList<PortDefinition> inputs, Action execute) : IExecutableNode
    {
        public NodeDescriptor Descriptor { get; } = new(type, "1.0.0", inputs, [new("Value", typeof(int))]);
        public IReadOnlyList<string> ValidateParameters(JsonElement parameters) => [];
        public ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        { execute(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Value"] = 1 })); }
    }
}

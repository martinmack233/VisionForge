using System.Text.Json;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public abstract class NodeBase<TParameters> : IExecutableNode where TParameters : new()
{
    public abstract NodeDescriptor Descriptor { get; }
    public IReadOnlyList<string> ValidateParameters(JsonElement parameters)
    {
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object) return ["Parameters must be a JSON object."];
            return Validate(parameters.Deserialize<TParameters>(JsonDefaults.Options) ?? throw new ArgumentException("Missing parameters."));
        }
        catch (Exception ex) { return [ex.Message]; }
    }
    protected virtual IReadOnlyList<string> Validate(TParameters parameters) => [];
    public abstract ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken);
    protected static NodeDescriptor Describe(string type, PortDefinition[] inputs, PortDefinition[] outputs) =>
        new(type, "1.0.0", inputs, outputs);
}

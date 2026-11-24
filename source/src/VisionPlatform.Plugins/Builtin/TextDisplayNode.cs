using System.ComponentModel;
using System.Globalization;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public sealed record TextDisplayParameters
{
    [DisplayName("文本前缀")]
    public string Prefix { get; init; } = "";
}

/// <summary>Strongly typed text endpoint. It never guesses a metric from an image.</summary>
[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class TextDisplayNode : NodeBase<TextDisplayParameters>
{
    public override NodeDescriptor Descriptor => Describe("TextDisplay",
        [new("String", typeof(string), false), new("Number", typeof(double), false),
         new("Integer", typeof(int), false), new("Boolean", typeof(bool), false)],
        [new("Text", typeof(string))]);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connected = Descriptor.InputPorts.Where(p => context.Inputs.ContainsKey(p.Name)).ToArray();
        if (connected.Length != 1) throw new ArgumentException("TextDisplay requires exactly one String, Number, Integer or Boolean input.");
        var text = connected[0].Name switch
        {
            "String" => context.Input<string>("String"),
            "Integer" => context.Input<int>("Integer").ToString(CultureInfo.InvariantCulture),
            "Boolean" => context.Input<bool>("Boolean") ? "True" : "False",
            "Number" => FormatNumber(context.Input<double>("Number")),
            _ => throw new ArgumentException("Unsupported text input.")
        };
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Text"] = context.Parameters<TextDisplayParameters>().Prefix + text }));
    }

    private static string FormatNumber(double value) => double.IsFinite(value)
        ? value.ToString("G17", CultureInfo.InvariantCulture)
        : throw new ArgumentException("TextDisplay Number must be finite.");
}

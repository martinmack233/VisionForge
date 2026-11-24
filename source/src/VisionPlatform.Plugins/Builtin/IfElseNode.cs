using System.ComponentModel;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

public sealed record IfElseParameters
{
    [DisplayName("反转条件")]
    public bool InvertCondition { get; init; }
}

/// <summary>Only the selected control signal is produced; Runtime gates nodes before invoking them.</summary>
public sealed class IfElseNode : NodeBase<IfElseParameters>
{
    public override NodeDescriptor Descriptor => Describe("IfElse", [new("Condition", typeof(bool))],
        [NodePorts.Control("True"), NodePorts.Control("False")]);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var condition = context.Input<bool>("Condition") ^ context.Parameters<IfElseParameters>().InvertCondition;
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object>
        { [condition ? "True" : "False"] = ControlSignal.Active }));
    }
}

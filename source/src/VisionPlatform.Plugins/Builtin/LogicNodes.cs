using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class LogicAndNode : NodeBase<LogicBinaryOptions>
{
    public override NodeDescriptor Descriptor => Describe("LogicAnd",
        [new("ValueA", typeof(bool), false), new("ValueB", typeof(bool), false)], [new("Result", typeof(bool))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<LogicBinaryOptions>();
        var a = context.Inputs.ContainsKey("ValueA") ? context.Input<bool>("ValueA") : p.ValueA;
        var b = context.Inputs.ContainsKey("ValueB") ? context.Input<bool>("ValueB") : p.ValueB;
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = BooleanLogic.And(a, b) }));
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class LogicOrNode : NodeBase<LogicBinaryOptions>
{
    public override NodeDescriptor Descriptor => Describe("LogicOr",
        [new("ValueA", typeof(bool), false), new("ValueB", typeof(bool), false)], [new("Result", typeof(bool))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<LogicBinaryOptions>();
        var a = context.Inputs.ContainsKey("ValueA") ? context.Input<bool>("ValueA") : p.ValueA;
        var b = context.Inputs.ContainsKey("ValueB") ? context.Input<bool>("ValueB") : p.ValueB;
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = BooleanLogic.Or(a, b) }));
    }
}

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class LogicNotNode : NodeBase<LogicNotOptions>
{
    public override NodeDescriptor Descriptor => Describe("LogicNot", [new("Value", typeof(bool), false)], [new("Result", typeof(bool))]);
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<LogicNotOptions>();
        var value = context.Inputs.ContainsKey("Value") ? context.Input<bool>("Value") : p.Value;
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = BooleanLogic.Not(value) }));
    }
}

using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;

[VisionPlatform.Contracts.Collections.SubflowSafe]
public sealed class CompareNode : NodeBase<CompareOptions>
{
    public override NodeDescriptor Descriptor => Describe("Compare",
        [new("ValueA", typeof(object)) { Constraint = PortValueConstraint.Numeric },
         new("ValueB", typeof(object), false) { Constraint = PortValueConstraint.Numeric },
         new("Operator", typeof(CompareOperator), false)], [new("Result", typeof(bool))]);
    protected override IReadOnlyList<string> Validate(CompareOptions parameters) => NumericComparer.Validate(parameters);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var p = context.Parameters<CompareOptions>();
        var valueB = context.Inputs.ContainsKey("ValueB") ? context.Input<object>("ValueB") : p.ValueB;
        var operation = context.Inputs.ContainsKey("Operator") ? context.Input<CompareOperator>("Operator") : p.Operator;
        var result = NumericComparer.Compare(context.Input<object>("ValueA"), valueB, operation, p.Tolerance);
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = result }));
    }
}

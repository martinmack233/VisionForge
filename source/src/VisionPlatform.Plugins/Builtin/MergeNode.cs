using System.ComponentModel;
using System.Globalization;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Plugins.Builtin;

public sealed record MergeParameters
{
    [DisplayName("文本输出包含分支名")]
    public bool IncludeBranchNameInText { get; init; }
}

public sealed class MergeNode : NodeBase<MergeParameters>, IExclusiveBranchMergeNode
{
    public IReadOnlyList<BranchMergeInput> Branches { get; } =
        [new("TrueControl", "TrueValue"), new("FalseControl", "FalseValue")];
    public override NodeDescriptor Descriptor => Describe("Merge",
        [NodePorts.Control("TrueControl", true), new("TrueValue", typeof(object)),
         NodePorts.Control("FalseControl", true), new("FalseValue", typeof(object))],
        [new("Value", typeof(object)), new("ActiveBranch", typeof(string)), new("Text", typeof(string), false),
         new("Number", typeof(double), false), new("Boolean", typeof(bool), false), new("Image", typeof(ImageFrame), false),
         new("Result", typeof(InspectionReport), false), new("Communication", typeof(CommunicationStatus), false)]);

    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = Branches.Where(b => context.TryInput<ControlSignal>(b.ControlPort, out var signal) && signal.IsActive).ToArray();
        if (active.Length != 1) throw new ArgumentException("Merge requires exactly one active current branch.");
        var branch = active[0]; var value = context.Input<object>(branch.ValuePort);
        var name = branch.ControlPort == "TrueControl" ? "True" : "False";
        var values = new Dictionary<string, object> { ["Value"] = value, ["ActiveBranch"] = name };
        IDisposable[] owned = [];
        if (value is ImageFrame image)
        {
            var share = image.Share(); values["Value"] = share; values["Image"] = share; owned = [share];
        }
        else if (value is InspectionReport report) values["Result"] = report;
        else if (value is CommunicationStatus status) values["Communication"] = status;
        else if (value is bool boolean) values["Boolean"] = boolean;
        else if (value is string text)
            values["Text"] = context.Parameters<MergeParameters>().IncludeBranchNameInText ? name + ": " + text : text;
        else if (NodePorts.IsNumeric(value.GetType()))
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number)) throw new ArgumentException("Merge numeric output must be finite.");
            values["Number"] = number;
        }
        return ValueTask.FromResult(new NodeOutput(values, owned));
    }
}

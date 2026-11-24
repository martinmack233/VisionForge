using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;

namespace VisionPlatform.Editor.Core.Parameters;
public sealed record SubflowProblem(string? NodeId, string? Port, string Message)
{ public string Label => (NodeId is null ? "子流程" : "节点 " + NodeId + (Port is null ? "" : "." + Port)) + "：" + Message; }
public static class SubflowProblems
{
    public static IReadOnlyList<SubflowProblem> Find(NodeRegistry registry, SubflowDefinition body)
    {
        var result = new List<SubflowProblem>();
        foreach (var spec in body.Nodes.Where(n => n.IsEnabled))
        {
            try
            {
                var node = registry.Create(spec.NodeTypeId);
                foreach (var port in node.Descriptor.InputPorts.Where(p => p.Required))
                    if (!body.Connections.Any(e => e.TargetNodeId == spec.NodeId && e.TargetPort == port.Name)) result.Add(new(spec.NodeId, port.Name, "缺少必需输入，请连接对应类型的数据端口。"));
                if (spec.NodeId == body.OutputNodeId && !body.Connections.Any(e => e.TargetNodeId == spec.NodeId && e.TargetPort is "OCR" or "Measurement" or "Decision" or "Text" or "Number" or "IsOk"))
                    result.Add(new(spec.NodeId, null, "尚未收集业务结果；只连接ROI或置信度不能形成每项结果。"));
            }
            catch (Exception ex) { result.Add(new(spec.NodeId, null, ex.Message)); }
        }
        foreach (var edge in body.Connections)
        {
            var source = body.Nodes.FirstOrDefault(n => n.NodeId == edge.SourceNodeId); var target = body.Nodes.FirstOrDefault(n => n.NodeId == edge.TargetNodeId);
            if (source is null || target is null) { result.Add(new(target?.NodeId ?? source?.NodeId, null, "连线引用了不存在的节点。")); continue; }
            try
            {
                var output = NodePorts.Outputs(registry.Create(source.NodeTypeId).Descriptor).FirstOrDefault(p => p.Name == edge.SourcePort);
                var input = NodePorts.Inputs(registry.Create(target.NodeTypeId).Descriptor).FirstOrDefault(p => p.Name == edge.TargetPort);
                if (output is null || input is null || !NodePorts.Compatible(output, input)) result.Add(new(target.NodeId, edge.TargetPort, $"类型不匹配：{source.NodeId}.{edge.SourcePort} → {target.NodeId}.{edge.TargetPort}。"));
            }
            catch (Exception ex) { result.Add(new(target.NodeId, edge.TargetPort, ex.Message)); }
        }
        foreach (var error in SubflowValidation.Validate(registry, body))
        {
            var spec = body.Nodes.FirstOrDefault(n => error.Contains("'" + n.NodeId + "'", StringComparison.Ordinal) || error.Contains("'" + n.NodeId + ".", StringComparison.Ordinal));
            result.Add(new(spec?.NodeId, null, error));
        }
        return result;
    }
}

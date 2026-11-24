using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Recognition;

namespace VisionPlatform.Plugins.Builtin;

public sealed record YoloDetectionRoiOptions
{
    [DisplayName("保留类别 ID（逗号分隔，留空为全部）")] public string ClassIds { get; init; } = "";
    [DisplayName("最小置信度"), Range(0, 1)] public double MinimumConfidence { get; init; }
    [DisplayName("筛选后目标索引（从0开始）"), Range(0, 10000)] public int DetectionIndex { get; init; }
    [DisplayName("显式使用变换后的轴对齐包围矩形")] public bool AxisAlignedEnvelope { get; init; }
}

/// <summary>Adapts one current YOLO target to the existing ROI contract. No target means no ROI output.</summary>
public sealed class YoloDetectionRoiNode : NodeBase<YoloDetectionRoiOptions>
{
    public override NodeDescriptor Descriptor => Describe("YoloDetectionRoi",
        [new("Result", typeof(YoloInferenceResult)), new("CoordinateTransform", typeof(CoordinateTransform2D), false)],
        [new("ROI", typeof(RotatedRectRoi), false), new("Found", typeof(bool)), new("Count", typeof(int)),
            new("ClassId", typeof(int), false), new("Label", typeof(string), false), new("Confidence", typeof(double), false)]);
    protected override IReadOnlyList<string> Validate(YoloDetectionRoiOptions p)
    {
        var errors = new List<string>();
        if (!double.IsFinite(p.MinimumConfidence) || p.MinimumConfidence is < 0 or > 1) errors.Add("MinimumConfidence must be in [0,1].");
        if (p.DetectionIndex is < 0 or > 10000) errors.Add("DetectionIndex must be in [0,10000].");
        try { YoloInferenceValidation.ParseClasses(p.ClassIds); } catch (ArgumentException e) { errors.Add(e.Message); }
        return errors;
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = context.Input<YoloInferenceResult>("Result"); var p = context.Parameters<YoloDetectionRoiOptions>();
        if (!result.Success) throw new InvalidOperationException(result.FailureReason ?? "上游AI结果失败，没有可用目标ROI。");
        var classes = YoloInferenceValidation.ParseClasses(p.ClassIds);
        var targets = result.Detections.Where(d => d.Confidence >= p.MinimumConfidence && (classes.Length == 0 || classes.Contains(d.ClassId))).ToArray();
        var values = new Dictionary<string, object> { ["Found"] = p.DetectionIndex < targets.Length, ["Count"] = targets.Length };
        if (p.DetectionIndex >= targets.Length) return ValueTask.FromResult(new NodeOutput(values));
        var target = targets[p.DetectionIndex];
        if (!double.IsFinite(target.Confidence) || target.Confidence is < 0 or > 1) throw new ArgumentException("AI目标的置信度无效。");
        var region = target.Bounds.ToRotated(result.CoordinateSpaceId); region.Validate();
        if (context.TryInput<CoordinateTransform2D>("CoordinateTransform", out var transform))
        {
            if (transform.SourceSpaceId != result.CoordinateSpaceId) throw new ArgumentException("检测框与坐标变换的源空间不一致。");
            var center = transform.Map(new(region.CenterX, region.CenterY));
            var sx = Math.Sqrt(transform.M11 * transform.M11 + transform.M21 * transform.M21);
            var sy = Math.Sqrt(transform.M12 * transform.M12 + transform.M22 * transform.M22);
            var dot = transform.M11 * transform.M12 + transform.M21 * transform.M22;
            if (!new[] { center.X, center.Y, sx, sy, dot }.All(double.IsFinite) || sx <= 0 || sy <= 0) throw new ArgumentException("坐标变换包含无效或退化几何。");
            if (p.AxisAlignedEnvelope)
            {
                var corners = region.Corners().Select(transform.Map).ToArray();
                var left = corners.Min(c => c.X); var right = corners.Max(c => c.X); var top = corners.Min(c => c.Y); var bottom = corners.Max(c => c.Y);
                region = new((left + right) / 2, (top + bottom) / 2, right - left, bottom - top, 0, transform.TargetSpaceId);
            }
            else
            {
                if (Math.Abs(dot) > 1e-9 * sx * sy) throw new ArgumentException("该变换产生剪切四边形；不能冒充旋转矩形。需要时显式选择轴对齐包围矩形。");
                region = new(center.X, center.Y, region.Width * sx, region.Height * sy, Math.Atan2(transform.M21, transform.M11) * 180 / Math.PI, transform.TargetSpaceId);
            }
        }
        region.Validate();
        values["ROI"] = region; values["ClassId"] = target.ClassId; values["Label"] = target.Label; values["Confidence"] = target.Confidence;
        return ValueTask.FromResult(new NodeOutput(values));
    }
}

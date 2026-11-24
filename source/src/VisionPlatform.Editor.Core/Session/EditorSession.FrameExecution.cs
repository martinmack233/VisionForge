using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Editor.Core.Session;

public sealed partial class EditorSession
{
    /// <summary>The caller retains its frame. A separate shared lease is transferred to the current Runtime run.</summary>
    public async Task RunWithFrameAsync(string sourceNodeId, ImageFrame frame, CameraFrameInfo? info = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsDisposed) throw new ObjectDisposedException(nameof(frame));
        if (info is not null && info.FrameId != frame.FrameId) throw new ArgumentException("采集元数据与冻结图像 frameId 不一致。");
        using var borrowed = frame.Share();
        var request = _requests; Cancel(); await _operationGate.WaitAsync(); SessionExecution? execution = null;
        try
        {
            CheckAlive(); if (request != _requests) return; SetBusy(true, false);
            var project = SnapshotProject(); var source = project.Nodes.SingleOrDefault(n => n.NodeId == sourceNodeId)
                ?? throw new ArgumentException("冻结帧绑定的工程节点不存在。");
            if (!source.IsEnabled) throw new InvalidOperationException("已禁用的图像源不能检测冻结帧。");
            if (source.NodeTypeId is not ("VirtualCamera" or "CameraInput" or "ImageInput")) throw new ArgumentException("冻结帧仅可绑定图像输入或相机源节点。");
            if (source.NodeTypeId != "ImageInput" && info is null) throw new ArgumentException("相机冻结帧必须具有真实的采集序号与 frameId。");
            var revision = Revision; var root = ResourceRoot; ClearResults();
            SetStatus($"正在检测冻结帧 {borrowed.FrameId}（不重新采集）…");
            using var cancellation = new CancellationTokenSource(); _runCancellation = cancellation;
            var ownedFrame = borrowed.Share();
            var values = new Dictionary<string, object> { ["Image"] = ownedFrame };
            if (source.NodeTypeId != "ImageInput") values["FrameInfo"] = info!;
            var snapshot = new NodeOutput(values, [ownedFrame]);
            execution = await Task.Run(async () => new SessionExecution(
                await _executor.ExecuteWithSourceAsync(project.ToGraph(), sourceNodeId, snapshot, root, cancellation.Token), []));
            if (_disposed || revision != Revision || cancellation.IsCancellationRequested)
            { SetStatus("冻结帧检测已取消或工程已变化，结果未发布。"); return; }
            Publish(execution); execution = null;
            SetStatus(StatusMessage + $" · 冻结帧 #{info?.Sequence.ToString() ?? "—"} · frameId={borrowed.FrameId}");
        }
        finally { _runCancellation = null; execution?.Dispose(); SetBusy(false, false); _operationGate.Release(); }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Core.Models;

namespace VisionPlatform.Editor.Wpf.Devices;

public sealed record CameraSourceChoice(string Id, string Name);

public sealed partial class DevicesViewModel
{
    private string _selectedProviderId = "virtual-folder";
    private CameraDeviceInfo? _selectedDevice;
    private bool _draftDirty, _nodeWritten, _deviceApplied, _writingNode, _readingNode, _configurationConflict, _stopping;
    private string? _lastNodeConfiguration;
    private string? _boundNodeId;
    private readonly Dictionary<string, JsonElement> _hardwareDraft = new(StringComparer.Ordinal);
    private readonly Stopwatch _receiveClock = new(), _detectClock = new();
    private long _rateFrames, _detected, _dropped;
    private bool _detecting;
    private Channel<DetectionFrame>? _detectionQueue;
    private CancellationTokenSource? _detectionCancellation;
    private Task? _detectionTask;
    private int _queued;
    private Guid? _lastDetectionFrameId;
    private sealed record DetectionFrame(ImageFrame Frame, CameraFrameInfo Info) : IDisposable
    { public void Dispose() => Frame.Dispose(); }

    public Action<string, string, string, CameraOpenOptions>? ConfigureDeviceNode { get; set; }
    public IReadOnlyList<CameraSourceChoice> DeviceSources { get; } =
        [new("virtual-folder", "文件夹虚拟相机"), new("boq", "博峤相机（可选 SDK）")];
    public string SelectedProviderId
    {
        get => _selectedProviderId;
        set
        {
            if (!CanEditConnection && !_readingNode) return;
            if (!Set(ref _selectedProviderId, value)) return;
            _selectedDevice = Devices.FirstOrDefault(d => d.ProviderId == value);
            Notify(nameof(SelectedDevice)); Notify(nameof(AvailableDevices)); Notify(nameof(VirtualOptionsVisibility)); DraftChanged();
        }
    }
    public CameraDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set { if (Set(ref _selectedDevice, value)) DraftChanged(); }
    }
    public IReadOnlyList<CameraDeviceInfo> AvailableDevices => Devices.Where(d => d.ProviderId == SelectedProviderId).ToArray();
    public Visibility VirtualOptionsVisibility => SelectedProviderId == "virtual-folder" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectionDraftVisibility => _camera is null ? Visibility.Visible : Visibility.Collapsed;
    public string ConnectionSummary => _camera is { } camera ? $"{camera.Info.DisplayName}\n{camera.Info.ProviderId} / {camera.Info.DeviceId}\n连接后只在下方设备能力区修改参数；关闭设备可更换来源。" : "选择源和设备后配置，再打开。";
    public bool IsFrozen => PreviewImage is not null && !IsAcquiring;
    public string FrameState => PreviewImage is null ? "无图像" : IsAcquiring ? "实时预览" : "冻结帧（非实时）";
    public string ProviderDiagnostic => _session.CameraSession?.Providers.Diagnostics.GetValueOrDefault(SelectedProviderId) ?? _session.CameraSession?.Providers.AdapterDiagnostic ?? "";
    public string DisplayDeviceState => _stopping ? "停止中" : _camera?.State switch
    {
        CameraState.Open => IsBusy ? "正在操作" : "已连接",
        CameraState.Acquiring => "采集中",
        CameraState.Faulted => "故障",
        CameraState.Disposed => "已关闭",
        _ => IsBusy ? "正在操作" : "未连接"
    };
    public bool IsDetecting => _detecting;
    public Guid? LastDetectionFrameId => _lastDetectionFrameId;
    public long DetectedFrames => Interlocked.Read(ref _detected);
    public long DroppedDetectionFrames => Interlocked.Read(ref _dropped);
    public int QueuedFrames => Volatile.Read(ref _queued);
    public string Statistics => $"采集：{Rate(_rateFrames, _receiveClock)} fps · 检测：{Rate(DetectedFrames, _detectClock)} fps · 等待：{QueuedFrames}/1 · 丢弃：{DroppedDetectionFrames}";
    public string ConfigurationState => _configurationConflict ? "工程配置已变化；请关闭设备后重新读取工程配置。当前草稿尚未覆盖。" :
        $"{(_draftDirty ? "草稿有修改" : _nodeWritten ? "已写入工程" : "草稿未写入工程")} · {(_deviceApplied ? "已应用到设备" : "未应用到设备")} · {(_session.HasChanges ? "工程未保存" : "工程已保存")}";
    public string ConfigurationError
    {
        get
        {
            try
            {
                ValidateId();
                if (SelectedProviderId == "virtual-folder")
                {
                    var options = Options(true);
                    if (!Directory.Exists(options.FolderPath)) return "图像文件夹不存在。";
                    var errors = VisionPlatform.Acquisition.VirtualCameraDevice.ValidateOptions(options);
                    return errors.Count == 0 ? "" : string.Join(" ", errors);
                }
                return SelectedDevice is null || SelectedDevice.ProviderId != SelectedProviderId ? "请选择已枚举的设备。" : "";
            }
            catch (Exception ex) { return ex.GetBaseException().Message; }
        }
    }
    public IReadOnlyList<CameraParameterEditor> VisibleParameters => Parameters.ToArray();
    public ICommand ClearPreviewCommand { get; private set; } = null!;
    public ICommand DetectFrozenCommand { get; private set; } = null!;
    public ICommand StartDetectionCommand { get; private set; } = null!;
    public ICommand ReloadNodeCommand { get; private set; } = null!;

    private void InitializePhase5()
    {
        ClearPreviewCommand = new DeviceCommand(_ => { ClearPreview(); return Task.CompletedTask; }, _ => !_disposed && !IsAcquiring && !IsBusy && PreviewImage is not null);
        DetectFrozenCommand = new DeviceCommand(_ => DetectFrozenAsync(), _ => Ready && IsFrozen && BoundCameraNode is not null);
        StartDetectionCommand = new DeviceCommand(_ => StartContinuousDetectionAsync(), _ => Ready && !IsAcquiring && _camera?.State == CameraState.Open && BoundCameraNode is not null && ConfigurationError.Length == 0);
        ReloadNodeCommand = new DeviceCommand(async _ => { await CloseAsync(); ReadNodeConfiguration(); RefreshState(); }, _ => Ready && !IsAcquiring);
    }
    private EditorNode? BoundCameraNode => _session.Nodes.FirstOrDefault(n => n.IsEnabled && n.TypeId is "VirtualCamera" or "CameraInput" &&
        n.Parameters.TryGetProperty("CameraId", out var id) && id.GetString() == CameraId &&
        (n.TypeId == "VirtualCamera" ? SelectedProviderId == "virtual-folder" :
            n.Parameters.TryGetProperty("ProviderId", out var provider) && provider.GetString() == SelectedProviderId &&
            n.Parameters.TryGetProperty("DeviceId", out var device) && device.GetString() == SelectedDevice?.DeviceId));

    public Task DetectFrozenAsync() => Work(async (generation, ct) =>
    {
        if (!IsFrozen || _preview is null || _frameInfo is null) throw new InvalidOperationException("请先单帧采集或停止预览，获得明确的冻结帧。");
        var source = BoundCameraNode ?? throw new InvalidOperationException("请先写入所选设备的相机节点并连接检测流程。");
        using var frame = _preview.Share(); var info = _frameInfo;
        using var cancellation = ct.Register(_session.Cancel);
        await _session.RunWithFrameAsync(source.NodeId, frame, info);
        if (Current(generation)) { _lastDetectionFrameId = info.FrameId; Notify(nameof(LastDetectionFrameId)); Status = $"已检测冻结帧：序号 {info.Sequence}，FrameId {info.FrameId}。{_session.StatusMessage}"; }
    });

    public async Task StartContinuousDetectionAsync()
    {
        if (IsAcquiring || IsBusy || _session.IsBusy) return;
        var source = BoundCameraNode ?? throw new InvalidOperationException("请先写入相机节点并连接检测流程。");
        _detectionCancellation?.Dispose();
        _detectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _project.Token);
        _detectionQueue = Channel.CreateBounded<DetectionFrame>(new BoundedChannelOptions(1)
        { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Interlocked.Exchange(ref _detected, 0); Interlocked.Exchange(ref _dropped, 0); _detectClock.Restart();
        _detecting = true;
        var queue = _detectionQueue; var token = _detectionCancellation.Token; var generation = _generation;
        _detectionTask = Task.Run(() => DetectQueueAsync(queue, source.NodeId, generation, token));
        await StartAsync();
        if (!IsAcquiring) { StopDetectionQueue(); if (_detectionTask is { } task) await task; DrainDetectionQueue(); }
        else Status = "连续检测运行中：等待队列最多1帧；繁忙时丢弃较旧待检帧，仅保留最新帧。停止会取消并释放等待帧。";
        RefreshState();
    }
    private void QueueDetectionFrame(CameraFrameInfo? info)
    {
        if (!_detecting || _detectionQueue is null || _preview is null || info is null || info.FrameId != _preview.FrameId) return;
        var item = new DetectionFrame(_preview.Share(), info);
        if (_detectionQueue.Writer.TryWrite(item)) { Interlocked.Exchange(ref _queued, 1); return; }
        if (_detectionQueue.Reader.TryRead(out var old)) { old.Dispose(); Interlocked.Increment(ref _dropped); }
        if (!_detectionQueue.Writer.TryWrite(item)) { item.Dispose(); Interlocked.Increment(ref _dropped); }
        else Interlocked.Exchange(ref _queued, 1);
    }
    private async Task DetectQueueAsync(Channel<DetectionFrame> queue, string sourceId, long generation, CancellationToken ct)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _queued, 0);
                using (item)
                {
                    ct.ThrowIfCancellationRequested();
                    var run = await _dispatcher.InvokeAsync(() =>
                    {
                        if (!Current(generation) || ct.IsCancellationRequested) return Task.CompletedTask;
                        return _session.RunWithFrameAsync(sourceId, item.Frame, item.Info);
                    });
                    using var cancellation = ct.Register(_session.Cancel);
                    await run.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _detected);
                    if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() =>
                    {
                        if (!Current(generation)) return;
                        _lastDetectionFrameId = item.Info.FrameId; Notify(nameof(LastDetectionFrameId)); Notify(nameof(Statistics));
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _streamCancellation?.Cancel();
            if (!_dispatcher.HasShutdownStarted) await _dispatcher.InvokeAsync(() => { if (Current(generation)) SetError(ex); });
        }
        finally
        {
            while (queue.Reader.TryRead(out var abandoned)) abandoned.Dispose();
            Interlocked.Exchange(ref _queued, 0);
        }
    }
    private void StopDetectionQueue()
    {
        _detectionCancellation?.Cancel(); _detectionQueue?.Writer.TryComplete();
        if (_detecting) _session.Cancel();
        _detecting = false;
    }
    private void DrainDetectionQueue()
    {
        if (_detectionQueue is { } queue) while (queue.Reader.TryRead(out var frame)) frame.Dispose();
        Interlocked.Exchange(ref _queued, 0); _detectionQueue = null;
        _detectionCancellation?.Dispose(); _detectionCancellation = null;
    }
    private void RecordReceivedFrame()
    {
        if (!IsAcquiring) { _rateFrames = 1; _receiveClock.Reset(); return; }
        if (!_receiveClock.IsRunning) _receiveClock.Start();
        _rateFrames++;
    }
    private static string Rate(long frames, Stopwatch watch) => frames < 2 || watch.Elapsed.TotalSeconds < .1 ? "未知" : (frames / watch.Elapsed.TotalSeconds).ToString("F1", CultureInfo.InvariantCulture);
    private void DraftChanged()
    {
        if (!_readingNode) { _draftDirty = true; _nodeWritten = false; _deviceApplied = false; }
        RefreshState();
    }
    private void SelectEnumeratedDevice()
    {
        var prior = _selectedDevice;
        _selectedDevice = AvailableDevices.FirstOrDefault(d => d.DeviceId == prior?.DeviceId) ?? AvailableDevices.FirstOrDefault();
        Notify(nameof(SelectedDevice)); Notify(nameof(AvailableDevices)); RefreshState();
    }
    private void ReadDeviceIdentity(EditorNode node)
    {
        _readingNode = true;
        var provider = node.TypeId == "VirtualCamera" ? "virtual-folder" : node.Parameters.GetProperty("ProviderId").GetString() ?? "boq";
        SelectedProviderId = provider;
        var device = node.TypeId == "VirtualCamera" ? "folder" : node.Parameters.GetProperty("DeviceId").GetString() ?? "";
        _selectedDevice = Devices.FirstOrDefault(d => d.ProviderId == provider && d.DeviceId == device) ?? new(device, provider, device, provider == "virtual-folder");
        Notify(nameof(SelectedDevice));
    }
    private void SynchronizeNodeConfiguration()
    {
        var node = _session.SelectedNode is { TypeId: "CameraInput" or "VirtualCamera" } selected ? selected :
            _session.Nodes.FirstOrDefault(n => n.NodeId == _boundNodeId) ?? BoundCameraNode;
        if (node is null || node.Parameters.GetRawText() == _lastNodeConfiguration) return;
        if (_draftDirty || _camera is not null)
        {
            _configurationConflict = true; _deviceApplied = false; Notify(nameof(ConfigurationState));
            if (_detecting) StopDetectionQueue();
            return;
        }
        ReadNodeConfiguration(); RefreshState();
    }
    private void RefreshPhase5State()
    {
        foreach (var name in new[] { nameof(ConfigurationError), nameof(ConfigurationState), nameof(DisplayDeviceState), nameof(FrameState), nameof(IsFrozen), nameof(IsDetecting), nameof(Statistics), nameof(QueuedFrames), nameof(VisibleParameters), nameof(ConnectionDraftVisibility), nameof(ConnectionSummary), nameof(ProviderDiagnostic) }) Notify(name);
        foreach (var command in new[] { ClearPreviewCommand, DetectFrozenCommand, StartDetectionCommand, ReloadNodeCommand }.OfType<DeviceCommand>()) command.Refresh();
    }
}

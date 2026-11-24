using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Devices;

/// <summary>Device interaction only. Vision algorithms are executed solely by the existing editor session.</summary>
public sealed partial class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IEditorSession _session;
    private readonly Action<string, CameraOpenOptions> _configureNode;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _project = new();
    private CancellationTokenSource? _operation, _streamCancellation;
    private Task? _stream;
    private ICameraDevice? _camera;
    private string? _openedCameraId;
    private ImageFrame? _preview;
    private CameraFrameInfo? _frameInfo;
    private long _generation, _framesReceived;
    private bool _busy, _acquiring, _disposed;
    private string _cameraId = "virtual-main", _folder = "", _frameRate = "5", _format = "自动识别", _status = "选择图像文件夹并打开虚拟相机。不会自动采集或执行算法。", _error = "";
    private bool _loop = true;

    public DevicesViewModel(IEditorSession session, Action<string, CameraOpenOptions> configureNode)
    {
        _session = session; _configureNode = configureNode; _dispatcher = Dispatcher.CurrentDispatcher;
        _generation = session.AcquisitionRevision; _session.PropertyChanged += SessionChanged;
        RefreshCommand = new DeviceCommand(_ => RefreshAsync(), _ => Ready);
        OpenCommand = new DeviceCommand(_ => OpenAsync(), _ => Ready && !IsAcquiring && _camera is null && ConfigurationError.Length == 0);
        CaptureCommand = new DeviceCommand(_ => CaptureAsync(), _ => Ready && !IsAcquiring && _camera?.State == CameraState.Open && ConfigurationError.Length == 0);
        StartCommand = new DeviceCommand(_ => StartAsync(), _ => Ready && !IsAcquiring && _camera?.State == CameraState.Open && ConfigurationError.Length == 0);
        StopCommand = new DeviceCommand(_ => FreezePreviewAsync(), _ => !_disposed && (IsAcquiring || IsBusy || PreviewImage is not null));
        CloseCommand = new DeviceCommand(_ => CloseAsync(), _ => !_disposed && (_camera is not null || IsAcquiring));
        ConfigureNodeCommand = new DeviceCommand(_ => ConfigureNodeAsync(), _ => Ready && !IsAcquiring && ConfigurationError.Length == 0);
        RunProjectCommand = new DeviceCommand(_ => RunProjectAsync(), _ => Ready && BoundCameraNode is not null && ConfigurationError.Length == 0);
        ApplyParameterCommand = new DeviceCommand(p => p is CameraParameterEditor row ? ApplyParameterAsync(row) : Task.CompletedTask,
            p => Ready && p is CameraParameterEditor { CanWrite: true });
        ReadNodeConfiguration();
        InitializePhase5();
    }

    public ObservableCollection<CameraDeviceInfo> Devices { get; } = [];
    public ObservableCollection<CameraParameterEditor> Parameters { get; } = [];
    public IReadOnlyList<string> PixelFormats { get; } = ["自动识别", "Mono8", "BayerRG8", "BayerGB8", "RGB8", "BGR8"];
    public string CameraId { get => _cameraId; set { if (Set(ref _cameraId, value)) DraftChanged(); } }
    public string FolderPath { get => _folder; set { if (Set(ref _folder, value)) DraftChanged(); } }
    public string FrameRateText { get => _frameRate; set { if (Set(ref _frameRate, value)) DraftChanged(); } }
    public bool Loop { get => _loop; set { if (Set(ref _loop, value)) DraftChanged(); } }
    public string PixelFormatChoice { get => _format; set { if (Set(ref _format, value)) DraftChanged(); } }
    /// <summary>Owned current preview reference. The viewport borrows and creates its own share.</summary>
    public ImageFrame? PreviewImage => _preview;
    public CameraFrameInfo? FrameInfo => _frameInfo;
    public string FrameDescription => _frameInfo is { } f
        ? $"{f.Width}×{f.Height} {f.PixelFormat} · Stride {f.Stride} · 序号 {f.Sequence}\n{f.SourceName}\nFrameId {f.FrameId} · {f.Timestamp:O}" : "尚无当前预览帧。";
    public long FramesReceived => _framesReceived;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public string DeviceState => _camera?.State.ToString() ?? "Closed";
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RefreshState(); } }
    public bool IsAcquiring { get => _acquiring; private set { if (Set(ref _acquiring, value)) RefreshState(); } }
    public bool CanEditConnection => Ready && _camera is null;
    public bool Ready => !_disposed && !IsBusy && !_session.IsBusy && !_stopping;
    /// <summary>View-only backpressure: wait for current asynchronous preview before requesting another frame.</summary>
    public Func<Task>? WaitForPreviewAsync { get; set; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ConfigureNodeCommand { get; }
    public ICommand RunProjectCommand { get; }
    public ICommand ApplyParameterCommand { get; }

    public Task InitializeAsync() => RefreshAsync();
    public Task RefreshAsync() => Work(async (generation, ct) =>
    {
        var manager = _session.CameraSession ?? throw new InvalidOperationException("当前编辑会话没有可用的采集服务。");
        var devices = await manager.EnumerateAsync(ct);
        if (!Current(generation)) return;
        Devices.Clear(); foreach (var device in devices.Take(128)) Devices.Add(device);
        SelectEnumeratedDevice();
        Status = $"已枚举{Devices.Count}个设备。请选择设备源和设备；枚举不会打开或采集。";
    });

    public Task OpenAsync() => Work(async (generation, ct) =>
    {
        await EnsureCameraAsync(generation, ct);
        if (!Current(generation)) return;
        await RefreshParametersAsync(ct); _deviceApplied = true; Status = "设备已打开，尚未采集图像。";
    });

    public Task CaptureAsync() => Work(async (generation, ct) =>
    {
        ClearPreview();
        var camera = await EnsureCameraAsync(generation, ct);
        var frame = await camera.CaptureSingleAsync(ct).ConfigureAwait(true);
        PublishFrame(frame, camera.LastFrame, generation, ct);
        if (!Current(generation)) return;
        await RefreshParametersAsync(ct); Status = "单帧预览已冻结，可检测当前帧，也可明确采集新帧检测。";
    }, clearOnError: true);

    public Task StartAsync() => Work(async (generation, ct) =>
    {
        if (IsAcquiring) throw new InvalidOperationException("当前设备已在连续采集。");
        if (_stream is { IsCompleted: false }) throw new InvalidOperationException("上一次采集正在停止，请等待后再启动。");
        var camera = await EnsureCameraAsync(generation, ct);
        _streamCancellation?.Dispose();
        _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _project.Token);
        await camera.StartAcquisitionAsync(_streamCancellation.Token);
        if (!Current(generation)) { await camera.StopAcquisitionAsync(); return; }
        ClearPreview(); IsAcquiring = true; Error = ""; Status = "正在连续预览；仅保留当前帧，按预览处理速度背压，不排队。";
        _rateFrames = 0; _receiveClock.Restart();
        var streamCancellation = _streamCancellation;
        _stream = Task.Run(() => PumpAsync(camera, generation, streamCancellation));
    }, clearOnError: true);

    /// <summary>Stops preview without closing the session-owned camera or permanently disabling the view model.</summary>
    public Task StopAsync() => StopCoreAsync(false);
    public Task FreezePreviewAsync() => StopCoreAsync(true);
    private async Task StopCoreAsync(bool preserveFrame)
    {
        _stopping = true; RefreshState();
        _operation?.Cancel(); _streamCancellation?.Cancel(); StopDetectionQueue();
        await _gate.WaitAsync();
        try
        {
            if (_camera is { } camera)
                try { await camera.StopAcquisitionAsync(); }
                catch (ObjectDisposedException) { }
            if (_stream is { } stream) await stream;
            if (_detectionTask is { } detection) await detection;
            DrainDetectionQueue();
            _stream = null; _streamCancellation?.Dispose(); _streamCancellation = null;
            IsAcquiring = false; _detecting = false; Notify(nameof(IsDetecting)); Notify(nameof(DeviceState));
            _receiveClock.Stop(); _detectClock.Stop();
            if (!preserveFrame) ClearPreview();
            Notify(nameof(IsFrozen)); Notify(nameof(FrameState));
            if (!_disposed) Status = preserveFrame ? "采集已停止，保留最后一帧（冻结）。设备保持打开；清空按钮仅释放预览。" : "采集已停止，预览资源已释放。";
        }
        catch (Exception ex) { if (!_disposed) SetError(ex); }
        finally { _stopping = false; _gate.Release(); RefreshState(); }
    }

    public async Task CloseAsync()
    {
        await StopAsync();
        await Work(async (generation, ct) =>
        {
            var id = _openedCameraId;
            if (id is not null && _session.CameraSession is { } manager) await manager.CloseDeviceAsync(id, ct);
            if (!Current(generation)) return;
            _camera = null; _openedCameraId = null; Parameters.Clear(); ClearPreview(); _deviceApplied = false; Status = "设备已关闭，连接和图像资源已释放。";
        });
    }

    public async Task ConfigureNodeAsync()
    {
        await FreezePreviewAsync();
        await Work((_, _) =>
        {
            ValidateId();
            _writingNode = true;
            try
            {
                if (SelectedProviderId == "virtual-folder") _configureNode(CameraId, Options(resolveFolder: false));
                else (ConfigureDeviceNode ?? throw new InvalidOperationException("当前宿主未配置工业相机节点写入命令。"))(CameraId, SelectedProviderId, SelectedDevice?.DeviceId ?? throw new ArgumentException("请选择设备。"), Options(false));
                _nodeWritten = true; _draftDirty = false;
                var written = BoundCameraNode;
                if (written is not null) { _boundNodeId = written.NodeId; _lastNodeConfiguration = written.Parameters.GetRawText(); }
            }
            finally { _writingNode = false; }
            Notify(nameof(ConfigurationState));
            Status = "相机配置已写入正式节点。请连接Image端口并保存工程；打开工程不会自动采集。";
            return Task.CompletedTask;
        });
    }

    public async Task RunProjectAsync()
    {
        await StopAsync();
        await Work(async (generation, ct) =>
        {
            if (BoundCameraNode is null)
                throw new InvalidOperationException("请先把所选设备写入相机节点并连接检测流程；预览帧不会隐式替代新帧采集。");
            using var registration = ct.Register(_session.Cancel);
            await _session.RunAsync();
            if (Current(generation)) Status = _session.StatusMessage;
        });
    }

    public Task ApplyParameterAsync(CameraParameterEditor parameter) => Work(async (generation, ct) =>
    {
        if (_camera is null || !Parameters.Contains(parameter)) throw new InvalidOperationException("参数不属于当前打开设备。");
        if (!parameter.Capability.Writable || parameter.Capability.RequiresStopped && IsAcquiring)
            throw new InvalidOperationException("该参数当前不可写；需要时先停止采集。");
        var appliedValue = parameter.Capability.Readable ? parameter.ToJson() : JsonDefaults.Element(true);
        await _camera.SetParameterAsync(parameter.Name, appliedValue, ct);
        if (!Current(generation)) return;
        if (parameter.Capability.Readable && parameter.Capability.Writable && SelectedProviderId != "virtual-folder")
            _hardwareDraft[parameter.Name] = appliedValue.Clone();
        await RefreshParametersAsync(ct); SyncDraftFromParameters();
        if (parameter.Capability.Readable) { _deviceApplied = true; _draftDirty = true; }
        Notify(nameof(ConfigurationState)); Status = "设备参数已应用；如需随工程保存，请点击写入相机节点。动作按钮不会写入工程。";
    });

    private async Task<ICameraDevice> EnsureCameraAsync(long generation, CancellationToken ct)
    {
        ValidateId(); var manager = _session.CameraSession ?? throw new InvalidOperationException("当前编辑会话没有可用的采集服务。");
        var camera = await manager.GetDeviceAsync(CameraId, SelectedProviderId,
            SelectedDevice?.DeviceId ?? (SelectedProviderId == "virtual-folder" ? "folder" : throw new ArgumentException("请选择设备。")), Options(resolveFolder: true), ct);
        ct.ThrowIfCancellationRequested();
        if (!Current(generation)) throw new OperationCanceledException(ct);
        _camera = camera; _openedCameraId = CameraId; RefreshState(); return camera;
    }

    private async Task PumpAsync(ICameraDevice camera, long generation, CancellationTokenSource cancellation)
    {
        string? error = null;
        try
        {
            await foreach (var frame in camera.ReadFramesAsync(cancellation.Token).ConfigureAwait(false))
            {
                var info = camera.LastFrame;
                var handedOff = false;
                try
                {
                    await _dispatcher.InvokeAsync(() => { handedOff = true; PublishFrame(frame, info, generation, cancellation.Token); QueueDetectionFrame(info); });
                }
                finally { if (!handedOff) frame.Dispose(); }
                if (cancellation.IsCancellationRequested) break;
                if (WaitForPreviewAsync is { } wait)
                    await (await _dispatcher.InvokeAsync(wait)).WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { error = Limited(ex.GetBaseException().Message); }
        finally
        {
            try { await camera.StopAcquisitionAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { error ??= Limited(ex.GetBaseException().Message); }
            if (!_dispatcher.HasShutdownStarted)
                await _dispatcher.InvokeAsync(() =>
                {
                    if (!Current(generation) || !ReferenceEquals(_streamCancellation, cancellation)) return;
                    IsAcquiring = false; Notify(nameof(DeviceState));
                    if (error is not null) { Error = error; Status = "采集失败：" + error; ClearPreview(); }
                    else if (!cancellation.IsCancellationRequested) Status = "采集结束。最后一帧已冻结，可单独清空。";
                    Notify(nameof(IsFrozen)); Notify(nameof(FrameState));
                });
            _detectionQueue?.Writer.TryComplete();
        }
    }

    private void PublishFrame(ImageFrame frame, CameraFrameInfo? info, long generation, CancellationToken ct)
    {
        if (!Current(generation) || ct.IsCancellationRequested) { frame.Dispose(); return; }
        var previous = _preview; _preview = frame; _frameInfo = info; _framesReceived++;
        Notify(nameof(PreviewImage)); Notify(nameof(FrameInfo)); Notify(nameof(FrameDescription)); Notify(nameof(FramesReceived));
        previous?.Dispose(); Notify(nameof(DeviceState));
        RecordReceivedFrame(); RefreshPhase5State();
    }

    private void ClearPreview()
    {
        var previous = _preview; _preview = null; _frameInfo = null;
        Notify(nameof(PreviewImage)); Notify(nameof(FrameInfo)); Notify(nameof(FrameDescription)); previous?.Dispose();
        RefreshPhase5State();
    }

    private async Task RefreshParametersAsync(CancellationToken ct)
    {
        if (_camera is not { } camera) return;
        var values = await camera.ReadParametersAsync(ct); Parameters.Clear();
        foreach (var capability in camera.Capabilities.Parameters.Take(256))
            Parameters.Add(new(capability, values.TryGetValue(capability.Name, out var value) ? value : default));
        RefreshState();
    }

    private void SyncDraftFromParameters()
    {
        string? Value(string name) => Parameters.FirstOrDefault(p => p.Name == name)?.ValueText;
        if (Value("FolderPath") is { } path) FolderPath = path;
        if (Value("FrameRate") is { } rate) FrameRateText = rate;
        if (Value("Loop") is { } loop && bool.TryParse(loop, out var b)) Loop = b;
        if (Value("PixelFormat") is { } format) PixelFormatChoice = format is "" or "Auto" or "null" ? "自动识别" : format;
    }

    private CameraOpenOptions Options(bool resolveFolder)
    {
        if (SelectedProviderId != "virtual-folder") return new() { Parameters = _hardwareDraft.Count == 0 ? null : new Dictionary<string, JsonElement>(_hardwareDraft) };
        if (string.IsNullOrWhiteSpace(FolderPath)) throw new ArgumentException("请选择本地图像文件夹。");
        if (!double.TryParse(FrameRateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || !double.IsFinite(rate) || rate <= 0)
            throw new ArgumentException("帧率必须是有限正数，使用小数点表示小数。");
        PixelFormat? format = PixelFormatChoice == "自动识别" ? null :
            Enum.TryParse<PixelFormat>(PixelFormatChoice, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new ArgumentException("输入像素格式无效。");
        return new() { FolderPath = resolveFolder ? System.IO.Path.GetFullPath(FolderPath, _session.ResourceRoot) : FolderPath,
            FrameRate = rate, Loop = Loop, PixelFormat = format };
    }

    private void ValidateId()
    {
        if (string.IsNullOrWhiteSpace(CameraId) || CameraId.Length > 128 || CameraId.Any(char.IsControl))
            throw new ArgumentException("相机会话标识需要1..128个可打印字符。");
    }

    private async Task Work(Func<long, CancellationToken, Task> action, bool clearOnError = false)
    {
        if (_disposed) return;
        await _gate.WaitAsync();
        CancellationTokenSource? operation = null;
        var generation = _generation;
        try
        {
            if (_disposed) return;
            IsBusy = true; Error = "";
            operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _project.Token); _operation = operation;
            await action(generation, operation.Token);
        }
        catch (OperationCanceledException) { if (Current(generation)) Status = "设备操作已取消，未发布过期图像。"; }
        catch (Exception ex) { if (Current(generation)) { if (clearOnError) ClearPreview(); SetError(ex); } }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
            operation?.Dispose(); IsBusy = false; _gate.Release(); RefreshState();
        }
    }

    private bool Current(long generation) => !_disposed && generation == _generation;
    private void SetError(Exception ex) { Error = Limited(ex.GetBaseException().Message); Status = "设备操作失败：" + Error; Notify(nameof(DeviceState)); }
    private static string Limited(string value) => value.Length <= 4096 ? value : value[..4096] + "…";
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEditorSession.AcquisitionRevision))
        {
            _generation = _session.AcquisitionRevision; _project.Cancel(); _project.Dispose(); _project = new();
            _operation?.Cancel(); _streamCancellation?.Cancel(); _camera = null; _openedCameraId = null;
            StopDetectionQueue();
            IsAcquiring = false; Parameters.Clear(); ClearPreview(); _framesReceived = 0; Notify(nameof(FramesReceived));
            Error = ""; Status = "工程输入已切换，旧设备预览已取消并清除。"; RefreshState();
        }
        if (e.PropertyName == nameof(IEditorSession.ProjectPath)) ReadNodeConfiguration();
        if (e.PropertyName == nameof(IEditorSession.Revision) && !_writingNode) SynchronizeNodeConfiguration();
        if (e.PropertyName == nameof(IEditorSession.SelectedNode) && _session.SelectedNode?.TypeId is "CameraInput" or "VirtualCamera" && !_writingNode) SynchronizeNodeConfiguration();
        if (e.PropertyName == nameof(IEditorSession.HasChanges)) Notify(nameof(ConfigurationState));
        if (e.PropertyName == nameof(IEditorSession.IsBusy)) RefreshState();
    }

    private void ReadNodeConfiguration()
    {
        var node = _session.SelectedNode is { TypeId: "CameraInput" or "VirtualCamera" } selected ? selected : _session.Nodes.FirstOrDefault(n => n.TypeId is "VirtualCamera" or "CameraInput"); if (node is null) return;
        try
        {
            var options = node.TypeId == "CameraInput" ? node.Parameters.GetProperty("Options").Deserialize<CameraOpenOptions>(JsonDefaults.Options) ?? new() : node.Parameters.Deserialize<CameraOpenOptions>(JsonDefaults.Options) ?? new();
            ReadDeviceIdentity(node);
            CameraId = node.Parameters.TryGetProperty("CameraId", out var id) ? id.GetString() ?? "virtual-main" : "virtual-main";
            FolderPath = options.FolderPath; FrameRateText = options.FrameRate.ToString("G", CultureInfo.InvariantCulture); Loop = options.Loop;
            PixelFormatChoice = options.PixelFormat?.ToString() ?? "自动识别";
            _hardwareDraft.Clear(); if (options.Parameters is not null) foreach (var pair in options.Parameters) _hardwareDraft[pair.Key] = pair.Value.Clone();
            _nodeWritten = true; _draftDirty = false; _lastNodeConfiguration = node.Parameters.GetRawText();
            _boundNodeId = node.NodeId;
        }
        catch (JsonException ex) { SetError(ex); }
        finally { _readingNode = false; _configurationConflict = false; }
    }

    private void RefreshState()
    {
        Notify(nameof(Ready)); Notify(nameof(CanEditConnection)); Notify(nameof(DeviceState));
        foreach (var row in Parameters) row.SetAvailability(Ready, IsAcquiring);
        foreach (var command in new[] { RefreshCommand, OpenCommand, CaptureCommand, StartCommand, StopCommand, CloseCommand,
                     ConfigureNodeCommand, RunProjectCommand, ApplyParameterCommand }.OfType<DeviceCommand>()) command.Refresh();
        RefreshPhase5State();
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _session.PropertyChanged -= SessionChanged;
        _lifetime.Cancel(); _project.Cancel(); _operation?.Cancel(); _streamCancellation?.Cancel();
        StopDetectionQueue(); DrainDetectionQueue();
        ClearPreview(); WaitForPreviewAsync = null; _lifetime.Dispose(); _project.Dispose(); RefreshState();
    }

    private sealed class DeviceCommand(Func<object?, Task> action, Func<object?, bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute(parameter);
        public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(parameter); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CameraParameterEditor : ObservableObject
{
    private string _text;
    private bool _canWrite;
    public CameraParameterCapability Capability { get; }
    public string Name => Capability.Name;
    public string DisplayName => Capability.DisplayName;
    public string ValueText { get => _text; set { if (Set(ref _text, value)) Notify(nameof(BooleanValue)); } }
    public bool BooleanValue { get => bool.TryParse(ValueText, out var b) && b; set => ValueText = value ? "true" : "false"; }
    public bool CanWrite { get => _canWrite; private set => Set(ref _canWrite, value); }
    public string AccessDescription => !Capability.Readable ? "不可读" : !Capability.Writable ? "只读" : Capability.RequiresStopped ? "停止采集后可写" : "可写";
    public string ApplyLabel => Capability.Readable ? "应用" : "执行动作";
    public Visibility ValueEditorVisibility => Capability.Readable ? Visibility.Visible : Visibility.Collapsed;
    public string UnitStepDescription => $"{Capability.Unit ?? ""}{(Capability.Step is { } step ? $" · 步长 {step:G}" : "")}";
    public string RangeDescription => Capability.AllowedValues is { Count: > 0 } values ? string.Join(" / ", values) :
        Capability.Minimum is { } min || Capability.Maximum is { } ? $"{Capability.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "—"} … {Capability.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "—"}" : "";
    public IReadOnlyList<string> AllowedValues => Capability.AllowedValues ?? [];
    public Visibility TextVisibility => Capability.Kind is CameraParameterKind.Boolean or CameraParameterKind.Enumeration ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BooleanVisibility => Capability.Kind == CameraParameterKind.Boolean ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EnumerationVisibility => Capability.Kind == CameraParameterKind.Enumeration ? Visibility.Visible : Visibility.Collapsed;
    public CameraParameterEditor(CameraParameterCapability capability, JsonElement value)
    {
        Capability = capability;
        _text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : value.GetRawText();
    }
    public void SetAvailability(bool ready, bool acquiring) => CanWrite = ready && Capability.Writable && (!Capability.RequiresStopped || !acquiring);
    public JsonElement ToJson() => Capability.Kind switch
    {
        CameraParameterKind.Boolean => bool.TryParse(ValueText, out var b) ? JsonDefaults.Element(b) : throw new ArgumentException(DisplayName + "需要True/False。"),
        CameraParameterKind.Integer => long.TryParse(ValueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? JsonDefaults.Element(i) : throw new ArgumentException(DisplayName + "需要整数。"),
        CameraParameterKind.Number => double.TryParse(ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? JsonDefaults.Element(n) : throw new ArgumentException(DisplayName + "需要有限数值。"),
        CameraParameterKind.Enumeration when !AllowedValues.Contains(ValueText) => throw new ArgumentException(DisplayName + "枚举值无效。"),
        _ => JsonDefaults.Element(ValueText)
    };
}

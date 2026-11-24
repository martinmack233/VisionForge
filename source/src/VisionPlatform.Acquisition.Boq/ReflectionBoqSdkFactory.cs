using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using VisionPlatform.Contracts.Acquisition;

namespace VisionPlatform.Acquisition.Boq;

/// <summary>Uses only audited public SDK metadata. Loading occurs on first explicit enumeration/open.</summary>
public sealed class ReflectionBoqSdkFactory(string sdkPath) : IBoqSdkFactory
{
    private readonly string _path = Path.GetFullPath(sdkPath);
    public IReadOnlyList<BoqSdkIdentity> Enumerate()
    {
        var runtime = SdkRuntime.For(_path);
        using var lease = runtime.Acquire();
        lock (runtime.Gate)
        {
            object?[] args = [0];
            var devices = runtime.CallApi("DiscoverCamera", args) as Array;
            var count = (int)args[0]!;
            if (count < 0 || count > 128 || (devices?.Length ?? 0) != count) throw new InvalidDataException("博峤SDK设备计数无效。");
            return devices?.Cast<object>().Select(device => new BoqSdkIdentity(
                Field(device, "Serial"), Field(device, "ModelName"), Field(device, "FactoryName"), Field(device, "UserName"))).ToArray() ?? [];
        }
    }
    public IBoqSdkCamera Connect(string serial)
    {
        var runtime = SdkRuntime.For(_path);
        var lease = runtime.Acquire();
        try
        {
            lock (runtime.Gate)
            {
                // Discover and connect share this SDK initialization lifetime. The public API connects
                // by identity from the active discovery table; a previous enumerate lease was closed.
                object?[] discoveryArguments = [0];
                var current = runtime.CallApi("DiscoverCamera", discoveryArguments) as Array;
                if (current is null || !current.Cast<object>().Any(d => Field(d, "Serial") == serial))
                    throw new IOException("指定博峤序列号当前未被SDK枚举到；请刷新设备列表。");
                var camera = runtime.CallApi("ConnectCameraBySerial", [serial])
                    ?? throw new IOException("博峤SDK未能连接指定序列号；请检查占用、驱动、网络和设备电源。");
                return new ReflectionCamera(runtime, camera, lease);
            }
        }
        catch { lease.Dispose(); throw; }
    }
    private static string Field(object value, string name) => (string?)value.GetType().GetField(name)?.GetValue(value) ?? "";

    private sealed class SdkRuntime
    {
        private static readonly ConcurrentDictionary<string, SdkRuntime> Runtimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lazy<Type> _api;
        private int _users;
        public object Gate { get; } = new();
        private SdkRuntime(string path)
        {
            _api = new(() =>
            {
                if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("博峤SDK要求x64进程。");
                if (!File.Exists(path)) throw new FileNotFoundException("缺少用户自带博峤SDK。请将BoqApi_CSharp.dll及厂商要求的依赖放到CameraAdapters/Boq目录；离线和虚拟相机仍可使用。", path);
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(path).GetType("BoqApi_CSharp.BoqApi", true)!;
            });
        }
        public static SdkRuntime For(string path) => Runtimes.GetOrAdd(path, p => new(p));
        public IDisposable Acquire()
        {
            lock (Gate)
            {
                if (_users == 0) CheckStatus(CallApi("Init", []), "Init");
                checked { _users++; }
                return new Lease(this);
            }
        }
        public object? CallApi(string method, object?[] args) => Invoke(_api.Value.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(_api.Value.FullName, method), null, args);
        private void Release()
        {
            lock (Gate)
            {
                if (--_users == 0) CheckStatus(CallApi("Deinit", []), "Deinit");
            }
        }
        private sealed class Lease(SdkRuntime runtime) : IDisposable
        {
            private SdkRuntime? _runtime = runtime;
            public void Dispose() => Interlocked.Exchange(ref _runtime, null)?.Release();
        }
    }

    private sealed class ReflectionCamera : IBoqSdkCamera
    {
        private readonly SdkRuntime _runtime;
        private readonly Type _type;
        private readonly object _gate = new();
        private object? _camera;
        private IDisposable? _lease;
        // These remain strongly rooted until native stop/disconnect has completed.
        private readonly Delegate _imageCallback, _disconnectCallback;
        private bool _started;
        private IReadOnlyList<CameraParameterCapability> _capabilities = [];
        public event Action<BoqSdkFrame>? FrameReceived;
        public event Action? Disconnected;
        public ReflectionCamera(SdkRuntime runtime, object camera, IDisposable lease)
        {
            _runtime = runtime; _camera = camera; _type = camera.GetType(); _lease = lease;
            var imageType = _type.Assembly.GetType("BoqApi_CSharp.ImageCallback", true)!;
            var disconnectType = _type.Assembly.GetType("BoqApi_CSharp.DisconnectCallback", true)!;
            _imageCallback = Delegate.CreateDelegate(imageType, this, GetType().GetMethod(nameof(Image), BindingFlags.NonPublic | BindingFlags.Instance)!);
            _disconnectCallback = Delegate.CreateDelegate(disconnectType, this, GetType().GetMethod(nameof(Disconnect), BindingFlags.NonPublic | BindingFlags.Instance)!);
            try { Call("SetDisconnectCallback", [_disconnectCallback]); }
            catch { _runtime.CallApi("DisconnectCamera", [camera]); _camera = null; throw; }
        }

        public IReadOnlyList<CameraParameterCapability> QueryCapabilities()
        {
            lock (_gate)
            {
                var items = new List<CameraParameterCapability>();
                foreach (var name in new[] { "ExposureTimeRaw", "GainRaw" })
                {
                    if (!TryUInt(name, out _)) continue;
                    double? min = ReadBound("ReadCamRegMin", name), max = ReadBound("ReadCamRegMax", name);
                    // Only raw numerical units are known from this API. Do not label raw ticks as microseconds or dB.
                    items.Add(new(name, name == "GainRaw" ? "增益（原始值）" : "曝光（原始值）", CameraParameterKind.Integer,
                        true, true, min, max) { Unit = "raw", Step = ReadBound("ReadCamRegInc", name) });
                }
                foreach (var name in new[] { "ExposureAuto", "GainAuto", "TriggerMode", "TriggerSource", "PixelFormat" })
                {
                    if (!TryString(name, out _)) continue;
                    // The SDK has no enumeration/access-mode discovery. Display the actual register but do not
                    // invent enum choices or writable access for the connected model.
                    items.Add(new(name, name switch { "ExposureAuto" => "自动曝光", "GainAuto" => "自动增益", "TriggerMode" => "触发模式", "TriggerSource" => "触发来源", _ => "设备像素格式" },
                        CameraParameterKind.String, true, false));
                }
                if (TryString("TriggerSource", out var triggerSource) && triggerSource == "Software" && TryString("TriggerMode", out var triggerMode) && triggerMode == "On")
                    items.Add(new("SoftwareTrigger", "软件触发（仅显式单次操作）", CameraParameterKind.Boolean, false, true, RequiresStopped: false));
                _capabilities = items.ToArray(); return _capabilities;
            }
        }
        public IReadOnlyDictionary<string, JsonElement> ReadParameters()
        {
            lock (_gate)
            {
                var values = new Dictionary<string, JsonElement>();
                foreach (var cap in _capabilities.Where(c => c.Readable))
                {
                    if (cap.Kind == CameraParameterKind.Integer && TryUInt(cap.Name, out var number)) values[cap.Name] = JsonSerializer.SerializeToElement(number);
                    else if (cap.Kind == CameraParameterKind.String && TryString(cap.Name, out var text)) values[cap.Name] = JsonSerializer.SerializeToElement(text);
                }
                return values;
            }
        }
        public void SetParameter(string name, JsonElement value)
        {
            lock (_gate)
            {
                var cap = _capabilities.SingleOrDefault(c => c.Name == name && c.Writable)
                    ?? throw new NotSupportedException("博峤设备参数不可写或未确认支持：" + name);
                if (name == "SoftwareTrigger")
                {
                    if (value.ValueKind != JsonValueKind.True) throw new ArgumentException("软件触发要求一次显式true操作。");
                    CheckStatus(Call("SoftwareTrigger", []), "SoftwareTrigger"); return;
                }
                if (!value.TryGetUInt32(out var number) || cap.Minimum is { } min && number < min || cap.Maximum is { } max && number > max)
                    throw new ArgumentException("博峤原始数值参数超出设备范围或不是UInt32。");
                CheckStatus(Call(name == "GainRaw" ? "SetCamGainRaw" : "SetCamExposureTime", [number]), name);
            }
        }
        public void GrabOne(uint timeoutMilliseconds) { lock (_gate) Call("GrabeOne", [_imageCallback, timeoutMilliseconds], [_imageCallback.GetType(), typeof(uint)]); }
        public void Start() { lock (_gate) { Call("MultiShotStart", [_imageCallback]); _started = true; } }
        public void Stop() { lock (_gate) { if (_started) { CheckStatus(Call("MultiShotStop", []), "MultiShotStop"); _started = false; } } }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_camera is null) return;
                var camera = _camera;
                Exception? error = null;
                try { if (_started) Stop(); }
                catch (Exception ex) { error = ex; }
                try { CheckStatus(_runtime.CallApi("DisconnectCamera", [camera]), "DisconnectCamera"); }
                catch (Exception ex) { error ??= ex; }
                finally { _camera = null; _lease?.Dispose(); _lease = null; GC.KeepAlive(_imageCallback); GC.KeepAlive(_disconnectCallback); }
                // DisconnectCamera is the vendor's explicit native camera teardown API; do not call a
                // second native destructor on the same pointer. The managed wrapper is now unreachable.
                if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
        private void Image(object camera, byte[] imageData, int width, int height, int imageSize, int pixelFormat, byte[] chunkData, int chunkSize)
        {
            try { FrameReceived?.Invoke(new(imageData, width, height, imageSize, pixelFormat, DateTimeOffset.UtcNow)); }
            catch { /* Consumer errors are captured by BoqCameraDevice; never propagate across unmanaged frames. */ }
        }
        private void Disconnect(object camera) { try { Disconnected?.Invoke(); } catch { } }
        private bool TryUInt(string name, out uint number)
        {
            object?[] args = [name, (uint)0];
            var ok = Convert.ToInt32(Call("ReadCamReg", args, [typeof(string), typeof(uint).MakeByRefType()])) == 0;
            number = ok ? (uint)args[1]! : 0; return ok;
        }
        private bool TryString(string name, out string value)
        {
            object?[] args = [name, ""];
            var ok = Convert.ToInt32(Call("ReadCamReg", args, [typeof(string), typeof(string).MakeByRefType()])) == 0;
            value = ok ? (string?)args[1] ?? "" : ""; return ok;
        }
        private double? ReadBound(string method, string name)
        {
            object?[] args = [name, (uint)0];
            return Convert.ToInt32(Call(method, args)) == 0 ? (uint)args[1]! : null;
        }
        private object? Call(string method, object?[] args, Type[]? signature = null)
        {
            ObjectDisposedException.ThrowIf(_camera is null, this);
            var member = signature is null ? _type.GetMethods().Single(m => m.Name == method && m.GetParameters().Length == args.Length)
                : _type.GetMethod(method, signature) ?? throw new MissingMethodException(_type.FullName, method);
            return Invoke(member, _camera, args);
        }
    }
    private static object? Invoke(MethodInfo method, object? target, object?[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static void CheckStatus(object? status, string operation)
    { if (status is not int code || code != 0) throw new IOException($"博峤 {operation} 返回错误码 {status ?? "null"}。"); }
}

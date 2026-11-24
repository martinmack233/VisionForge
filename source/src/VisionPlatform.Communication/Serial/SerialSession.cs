using System.IO.Ports;
using System.Text.RegularExpressions;
using VisionPlatform.Communication.Framing;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;

namespace VisionPlatform.Communication.Serial;

/// <summary>Session-owned connections and attempt ledger. No ports open until OpenAsync is explicitly called.</summary>
public sealed class SerialSession : IAsyncDisposable
{
    public const string ServiceKey = "vision.serial";
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _ports = new(StringComparer.Ordinal);
    private readonly HashSet<(string Node, string Endpoint, string Key)> _attempts = [];
    private readonly Queue<CommunicationStatus> _events = [];
    private readonly Func<ISerialTransport> _factory;
    private readonly int _maximumSendAttempts;
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;
    public Guid SessionId { get; }
    public int OpenPortCount { get { lock (_sync) return _ports.Count; } }
    public IReadOnlyList<CommunicationStatus> Events { get { lock (_sync) return _events.ToArray(); } }
    public SerialSession(Func<ISerialTransport>? factory = null, Guid? sessionId = null, CancellationToken lifetime = default,
        int maximumSendAttempts = 10000)
    {
        if (maximumSendAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSendAttempts));
        _factory = factory ?? (() => new SerialPortTransport()); _maximumSendAttempts = maximumSendAttempts;
        SessionId = sessionId ?? Guid.NewGuid(); _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }
    public static SerialSession For(IExecutionServices? services) => services is null
        ? throw new InvalidOperationException("Serial communication requires a runtime execution session.")
        : services.GetOrCreate(ServiceKey, () => new SerialSession(sessionId: services.SessionId, lifetime: services.LifetimeCancellation));

    public static IReadOnlyList<string> ValidateOpen(SerialOpenOptions? options)
    {
        if (options is null) return ["Serial open options are required."];
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ConnectionId) || options.ConnectionId.Length > 128) errors.Add("Serial ConnectionId must contain 1..128 characters.");
        if (options.PortName is null || !Regex.IsMatch(options.PortName, "^COM[1-9][0-9]{0,5}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) errors.Add("PortName must be an explicit COM port name, such as COM1.");
        if (options.BaudRate is < 1 or > 4000000) errors.Add("BaudRate must be in [1,4000000].");
        if (options.DataBits is < 5 or > 8) errors.Add("DataBits must be in [5,8].");
        if (!Enum.IsDefined(options.Parity)) errors.Add("Parity is invalid.");
        if (!Enum.IsDefined(options.StopBits) || options.StopBits == StopBits.None) errors.Add("StopBits is invalid.");
        if (options.TimeoutMilliseconds is < 1 or > 120000) errors.Add("Serial timeout must be in [1,120000] ms.");
        errors.AddRange(FrameCodec.Validate(options.Protocol)); return errors;
    }

    public async Task<SerialPortReference> OpenAsync(SerialOpenOptions options, CancellationToken ct = default)
    {
        var errors = ValidateOpen(options); if (errors.Count != 0) throw new ArgumentException(string.Join(" ", errors));
        using var deadline = Deadline(options.TimeoutMilliseconds, ct);
        Entry entry; bool created;
        lock (_sync)
        {
            CheckAlive();
            created = !_ports.TryGetValue(options.ConnectionId, out entry!);
            if (created) { entry = new Entry(options, _factory()); _ports.Add(options.ConnectionId, entry); }
            else if (entry.Options != options) throw new InvalidOperationException("Serial connection settings changed. Close the named port before reopening with new settings.");
        }
        if (!created)
        {
            try
            {
                await entry.Opened.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return new(SessionId, options.ConnectionId);
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new TimeoutException("Waiting for the serial port to open timed out.", error); }
            catch (OperationCanceledException error) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new IOException("The shared serial opening attempt failed or was closed.", error); }
        }
        try
        {
            await using var abortOnCancellation = new SerialCancellation(entry.Transport, deadline.Token);
            await entry.Transport.OpenAsync(options, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!entry.Transport.IsOpen) throw new IOException("Serial transport did not open.");
            entry.Reader = new FramedMessageReader(entry.Transport.Stream, options.Protocol);
            entry.Opened.TrySetResult();
            Log(new(CommunicationState.Connected, true, "Serial port opened."));
            return new(SessionId, options.ConnectionId);
        }
        catch (Exception error)
        {
            entry.Opened.TrySetCanceled(); await RemoveAndAbort(options.ConnectionId, entry).ConfigureAwait(false);
            Log(new(CommunicationState.Failed, false, "Serial open failed.", ErrorCode: error.GetType().Name));
            if (error is OperationCanceledException && !ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) throw new TimeoutException("Serial open timed out.", error);
            throw;
        }
    }

    public async Task<CommunicationStatus> SendAsync(SerialPortReference reference, string nodeId, string deliveryKey,
        string message, int timeoutMilliseconds, bool forceResend = false, CancellationToken ct = default)
    {
        ValidateTimeout(timeoutMilliseconds); ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(deliveryKey) || deliveryKey.Length > 256) throw new ArgumentException("DeliveryKey must contain 1..256 characters when sending.");
        var entry = Get(reference); var bytes = FrameCodec.Encode(message, entry.Options.Protocol);
        lock (_sync)
        {
            CheckAlive();
            var identity = (nodeId, entry.Options.PortName.ToUpperInvariant(), deliveryKey);
            if (_attempts.Contains(identity))
            {
                if (!forceResend) return Log(new(CommunicationState.DuplicateSuppressed, true, "Serial send attempt already registered; no bytes sent."));
            }
            else if (_attempts.Count >= _maximumSendAttempts)
                throw new InvalidOperationException("Serial delivery-key ledger is full. No new send attempt is allowed until a new execution session is started; old keys were retained.");
            _attempts.Add(identity);
        }
        using var deadline = Deadline(timeoutMilliseconds, ct);
        var acquired = false;
        try
        {
            await entry.Gate.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
            await using var abortOnCancellation = new SerialCancellation(entry.Transport, deadline.Token);
            await entry.Transport.Stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return Log(new(CommunicationState.Sent, true, "Serial frame accepted by the transport; remote delivery is not acknowledged.", bytes.Length));
        }
        catch (Exception error)
        {
            await RemoveAndAbort(reference.Id, entry).ConfigureAwait(false);
            Log(new(CommunicationState.Failed, false, "Serial send failed; attempt retained to prevent automatic resend.", ErrorCode: error.GetType().Name));
            if (error is OperationCanceledException && !ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) throw new TimeoutException("Serial write timed out; delivery is uncertain.", error);
            throw;
        }
        finally { if (acquired) entry.Gate.Release(); }
    }

    public async Task<string> ReceiveAsync(SerialPortReference reference, int timeoutMilliseconds, CancellationToken ct = default)
    {
        ValidateTimeout(timeoutMilliseconds); var entry = Get(reference);
        using var deadline = Deadline(timeoutMilliseconds, ct); var acquired = false;
        try
        {
            await entry.Gate.WaitAsync(deadline.Token).ConfigureAwait(false); acquired = true;
            var message = await entry.Reader!.ReadAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            Log(new(CommunicationState.Received, true, "Serial frame received.", FrameCodec.GetEncoding(entry.Options.Protocol.Encoding).GetByteCount(message)));
            return message;
        }
        catch (Exception error)
        {
            await RemoveAndAbort(reference.Id, entry).ConfigureAwait(false);
            Log(new(CommunicationState.Failed, false, "Serial receive failed; port closed to avoid reusing a partial frame.", ErrorCode: error.GetType().Name));
            if (error is OperationCanceledException && !ct.IsCancellationRequested && !_lifetime.IsCancellationRequested) throw new TimeoutException("Serial read timed out; port closed.", error);
            throw;
        }
        finally { if (acquired) entry.Gate.Release(); }
    }

    public async Task<CommunicationStatus> CloseAsync(SerialPortReference reference)
    {
        if (reference.SessionId != SessionId) throw new ArgumentException("Serial port reference belongs to another execution session.");
        Entry? entry; lock (_sync) { CheckAlive(); _ports.Remove(reference.Id, out entry); }
        if (entry is not null) await entry.Transport.AbortAsync().ConfigureAwait(false);
        return Log(new(CommunicationState.Closed, true, "Serial port closed."));
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_sync) { if (_disposed) return; _disposed = true; _lifetime.Cancel(); entries = _ports.Values.ToArray(); _ports.Clear(); }
        List<Exception>? errors = null;
        foreach (var entry in entries)
            try { entry.Opened.TrySetCanceled(); await entry.Transport.AbortAsync().ConfigureAwait(false); }
            catch (Exception error) { (errors ??= []).Add(error); }
        _lifetime.Dispose(); if (errors is not null) throw new AggregateException("Serial transports could not all be closed.", errors);
    }
    private async Task RemoveAndAbort(string id, Entry entry)
    {
        lock (_sync) if (_ports.TryGetValue(id, out var current) && ReferenceEquals(current, entry)) _ports.Remove(id);
        try { await entry.Transport.AbortAsync().ConfigureAwait(false); } catch { /* Preserve the original IO failure. */ }
    }
    private Entry Get(SerialPortReference reference)
    {
        lock (_sync)
        {
            CheckAlive();
            if (reference.SessionId != SessionId) throw new ArgumentException("Serial port reference belongs to another execution session.");
            return _ports.TryGetValue(reference.Id, out var entry) && entry.Transport.IsOpen && entry.Reader is not null
                ? entry : throw new IOException("Serial port is not open in this execution session.");
        }
    }
    private CancellationTokenSource Deadline(int milliseconds, CancellationToken ct)
    { var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token); linked.CancelAfter(milliseconds); return linked; }
    private CommunicationStatus Log(CommunicationStatus status)
    { lock (_sync) { _events.Enqueue(status); while (_events.Count > 256) _events.Dequeue(); } return status; }
    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void ValidateTimeout(int timeout) { if (timeout is < 1 or > 120000) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be in [1,120000] ms."); }
    private sealed class Entry(SerialOpenOptions options, ISerialTransport transport)
    {
        public SerialOpenOptions Options { get; } = options;
        public ISerialTransport Transport { get; } = transport;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FramedMessageReader? Reader { get; set; }
    }
}

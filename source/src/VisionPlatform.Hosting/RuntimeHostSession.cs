using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using VisionPlatform.Contracts.Hosting;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Hosting;

/// <summary>One explicitly controlled project per connection. Owns services, never executes on load.</summary>
public sealed class RuntimeHostSession : IAsyncDisposable
{
    private readonly NodeRegistry _registry;
    private readonly ProjectStore _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (string Fingerprint, HostResponse Response)> _mutationResponses = new(StringComparer.Ordinal);
    private readonly Queue<string> _mutationOrder = new();
    private ProjectDocument? _project;
    private string? _root;
    private RuntimeExecutionSession? _services;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private Guid? _runId;
    private HostResultSnapshot? _result;
    private HostSessionState _state = HostSessionState.Empty;
    private bool _cancellationRequested;
    private string? _error;
    private string? _projectionError;
    public Guid SessionId { get; } = Guid.NewGuid();
    public bool IsClosed { get { lock (_stateGate) return _state == HostSessionState.Closed; } }
    public RuntimeHostSession(NodeRegistry registry, IReadOnlyDictionary<string, string>? algorithmVersions = null, ILogger? logger = null)
    { _registry = registry; _store = new(registry, algorithmVersions); _logger = logger ?? NullLogger.Instance; }

    public async Task<HostResponse> HandleAsync(HostRequest request, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (request.ProtocolVersion != 1) return HostJson.Error(request.RequestId ?? "", HostErrorCodes.UnsupportedVersion, "Only protocol version 1 is supported.");
            if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 64 || request.RequestId.Any(char.IsControl))
                return HostJson.Error("", HostErrorCodes.InvalidRequest, "RequestId must contain 1..64 printable characters.");
            if (string.IsNullOrWhiteSpace(request.Operation) || request.Operation.Length > 32)
                return HostJson.Error(request.RequestId, HostErrorCodes.InvalidRequest, "An explicit operation is required.");
            if (IsClosed) return HostJson.Error(request.RequestId, HostErrorCodes.InvalidRequest, "The host session is closed.");
            var mutation = request.Operation is "load" or "run" or "cancel" or "close";
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Operation + "\n" + request.Payload?.GetRawText())));
            if (_mutationResponses.TryGetValue(request.RequestId, out var cached))
                return cached.Fingerprint == fingerprint ? cached.Response : HostJson.Error(request.RequestId, HostErrorCodes.InvalidRequest, "RequestId was reused with different content.");
            HostResponse response;
            try
            {
                response = request.Operation switch
                {
                    "load" => await LoadAsync(request, ct).ConfigureAwait(false),
                    "run" => StartRun(request),
                    "status" => ReadStatus(request),
                    "result" => ReadResult(request),
                    "cancel" => CancelRun(request),
                    "close" => await CloseCommandAsync(request).ConfigureAwait(false),
                    _ => HostJson.Error(request.RequestId, HostErrorCodes.UnknownOperation, "Unknown operation. Use load/run/status/result/cancel/close.")
                };
            }
            catch (HostCommandException ex) { response = HostJson.Error(request.RequestId, ex.Code, ex.Message); }
            catch (OperationCanceledException) { response = HostJson.Error(request.RequestId, HostErrorCodes.Cancelled, "The command was cancelled."); }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            { response = HostJson.Error(request.RequestId, HostErrorCodes.InvalidRequest, ex.Message); }
            catch (Exception ex)
            {
                _logger.LogError("Host operation {Operation} failed ({Type})", request.Operation, ex.GetType().Name);
                response = HostJson.Error(request.RequestId, HostErrorCodes.InternalError, ex.Message);
            }
            if (mutation && response.Success)
            {
                _mutationResponses.Add(request.RequestId, (fingerprint, response)); _mutationOrder.Enqueue(request.RequestId);
                while (_mutationOrder.Count > 128) _mutationResponses.Remove(_mutationOrder.Dequeue());
            }
            return response;
        }
        finally { _commands.Release(); }
    }
    private async Task<HostResponse> LoadAsync(HostRequest request, CancellationToken ct)
    {
        RequireIdle(); var payload = HostJson.Payload<LoadProjectCommand>(request);
        if (string.IsNullOrWhiteSpace(payload.ProjectPath)) throw new HostCommandException(HostErrorCodes.InvalidRequest, "ProjectPath is required.");
        var path = Path.GetFullPath(payload.ProjectPath);
        ProjectDocument project;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Project is missing or exceeds the 16 MiB document budget.");
            project = await _store.LoadAsync(path, ct).ConfigureAwait(false);
            if (project.Nodes.Count > 1000 || project.Connections.Count > 10000) throw new IOException("Project exceeds the 1000-node / 10000-connection host budget.");
            if (project.ProjectName.Length > 256 || project.Nodes.Any(n => n.NodeId.Length > 256 || n.Name.Length > 1024)) throw new IOException("Project/node names exceed the bounded host metadata limits.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new HostCommandException(HostErrorCodes.ProjectInvalid, ex.Message); }
        if (_services is not null)
        {
            try { await _services.DisposeAsync().ConfigureAwait(false); }
            catch
            {
                _services = null;
                lock (_stateGate) { _project = null; _root = null; _runId = null; _result = null; _state = HostSessionState.Failed; _error = "Previous project service cleanup failed; no project is loaded."; }
                throw;
            }
        }
        _services = new();
        lock (_stateGate)
        {
            _project = project; _root = Path.GetDirectoryName(path)!; _runId = null; _runTask = null; _result = null;
            _error = null; _projectionError = null; _cancellationRequested = false; _state = HostSessionState.Ready;
        }
        _logger.LogInformation("Host project loaded: {ProjectId}; {NodeCount} nodes", project.ProjectId, project.Nodes.Count);
        return HostJson.Ok(request, new { project.ProjectId, project.ProjectName, project.SchemaVersion, NodeCount = project.Nodes.Count, SessionId }, "Project validated; no algorithm has executed.");
    }
    private HostResponse StartRun(HostRequest request)
    {
        RequireIdle(); var p = HostJson.Payload(request, new RunProjectCommand());
        if (p.TimeoutMilliseconds is < 1 or > 3600000) throw new HostCommandException(HostErrorCodes.InvalidRequest, "Run timeout must be in [1,3600000] ms; native calls remain cooperatively cancellable.");
        lock (_stateGate)
        {
            if (_project is null || _services is null) throw new HostCommandException(HostErrorCodes.NotLoaded, "Load a valid project before run.");
            if (p.TargetNodeId is { } target && !_project.Nodes.Any(n => n.NodeId == target))
                throw new HostCommandException(HostErrorCodes.InvalidRequest, "TargetNodeId is absent from the loaded project.");
            var project = _project; var root = _root!; var services = _services; var id = Guid.NewGuid();
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _runCancellation.CancelAfter(p.TimeoutMilliseconds);
            _runId = id; _result = null; _error = null; _projectionError = null; _cancellationRequested = false; _state = HostSessionState.Running;
            var cancellation = _runCancellation;
            _runTask = Task.Run(() => ExecuteAsync(id, project, root, services, p.TargetNodeId, cancellation));
            return HostJson.Ok(request, new HostRunAccepted(id, SessionId), "Run accepted. Poll status/result with this RunId; no automatic retry.");
        }
    }
    private async Task ExecuteAsync(Guid hostRunId, ProjectDocument project, string root, RuntimeExecutionSession services, string? target, CancellationTokenSource cancellation)
    {
        HostResultSnapshot snapshot; string? projectionError = null;
        try
        {
            var executor = new RuntimeExecutor(_registry, _logger, services);
            using var run = target is null ? await executor.ExecuteAsync(project.ToGraph(), root, cancellation.Token).ConfigureAwait(false)
                : await executor.ExecuteToAsync(project.ToGraph(), target, root, cancellation.Token).ConfigureAwait(false);
            try { snapshot = HostResultProjector.Project(hostRunId, run); }
            catch (HostCommandException ex) when (ex.Code == HostErrorCodes.ResultTooLarge)
            {
                projectionError = ex.Message;
                snapshot = new(hostRunId, run.RunId, run.Status, run.Success, run.ExecutionTime.TotalMilliseconds, run.ErrorMessage, []);
            }
        }
        catch (Exception ex)
        {
            var cancelled = cancellation.IsCancellationRequested;
            snapshot = new(hostRunId, null, cancelled ? NodeExecutionStatus.Cancelled : NodeExecutionStatus.Failed,
                false, 0, cancelled ? "Execution was cancelled." : ex.Message, []);
        }
        lock (_stateGate)
        {
            _cancellationRequested |= cancellation.IsCancellationRequested;
            if (_state != HostSessionState.Closed && _runId == hostRunId)
            {
                _result = snapshot; _error = projectionError ?? snapshot.ErrorMessage; _projectionError = projectionError;
                _state = snapshot.Status == NodeExecutionStatus.Cancelled ? HostSessionState.Cancelled : snapshot.Success ? HostSessionState.Completed : HostSessionState.Failed;
            }
            _runCancellation = null;
        }
        cancellation.Dispose();
        _logger.LogInformation("Host run {RunId} finished: {Status}; {Milliseconds} ms", hostRunId, snapshot.Status, snapshot.ExecutionMilliseconds);
    }
    private HostResponse ReadStatus(HostRequest request)
    {
        lock (_stateGate) { RequireReference(request); return HostJson.Ok(request, Snapshot()); }
    }
    private HostResponse ReadResult(HostRequest request)
    {
        lock (_stateGate)
        {
            RequireReference(request);
            if (_runId is null) throw new HostCommandException(HostErrorCodes.RunNotFound, "No current run exists.");
            if (_result is null) throw new HostCommandException(HostErrorCodes.NotReady, "The current run has not completed.");
            if (_projectionError is not null) throw new HostCommandException(HostErrorCodes.ResultTooLarge, _projectionError);
            return HostJson.Ok(request, _result);
        }
    }
    private HostResponse CancelRun(HostRequest request)
    {
        lock (_stateGate)
        {
            RequireReference(request);
            if (_state != HostSessionState.Running) throw new HostCommandException(HostErrorCodes.NotReady, "There is no active run to cancel.");
            _cancellationRequested = true; _runCancellation?.Cancel(); return HostJson.Ok(request, Snapshot(), "Cancellation requested; the run remains Running until the engine releases its resources.");
        }
    }
    private void RequireReference(HostRequest request)
    {
        var reference = HostJson.Payload(request, new RunReference());
        if (_runId != reference.RunId) throw new HostCommandException(HostErrorCodes.RunNotFound, "RunId must identify the current execution; old or missing IDs are not accepted.");
    }
    private HostStatusSnapshot Snapshot() => new(SessionId, _state, _project?.ProjectName, _runId, _cancellationRequested, _error);
    private void RequireIdle()
    { lock (_stateGate) if (_state == HostSessionState.Running) throw new HostCommandException(HostErrorCodes.Busy, "A run is active; cancel/wait before loading or starting another run."); }
    private async Task<HostResponse> CloseCommandAsync(HostRequest request)
    { await CloseCoreAsync().ConfigureAwait(false); return HostJson.Ok(request, new { SessionId, State = HostSessionState.Closed }, "Session closed; execution and services released."); }
    private async Task CloseCoreAsync()
    {
        Task? running; var errors = new List<Exception>();
        lock (_stateGate)
        {
            if (_state == HostSessionState.Closed) return;
            _state = HostSessionState.Closed; _cancellationRequested = true;
            try { _lifetime.Cancel(); } catch (Exception ex) { errors.Add(ex); }
            try { _runCancellation?.Cancel(); } catch (Exception ex) { errors.Add(ex); }
            running = _runTask;
        }
        try { if (running is not null) await running.ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        try { if (_services is not null) await _services.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        finally
        {
            _services = null;
            lock (_stateGate) { _result = null; _project = null; _root = null; }
            _lifetime.Dispose(); _mutationResponses.Clear(); _mutationOrder.Clear();
        }
        if (errors.Count > 0) throw new AggregateException("Host session cleanup attempted all resources and reported failures.", errors);
    }
    public async ValueTask DisposeAsync()
    { await _commands.WaitAsync().ConfigureAwait(false); try { await CloseCoreAsync().ConfigureAwait(false); } finally { _commands.Release(); } }
}

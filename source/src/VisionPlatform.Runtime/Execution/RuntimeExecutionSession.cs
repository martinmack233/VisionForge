using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Runtime.Execution;

/// <summary>Owns project/run services independently of retained algorithm output resources.</summary>
public sealed class RuntimeExecutionSession : IExecutionServices, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
    private readonly List<string> _creationOrder = [];
    private readonly List<Task> _removals = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed;
    private Task? _disposeTask;
    public Guid SessionId { get; } = Guid.NewGuid();
    public CancellationToken LifetimeCancellation => _lifetimeToken;

    public RuntimeExecutionSession() => _lifetimeToken = _lifetime.Token;

    public T GetOrCreate<T>(string key, Func<T> factory) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key); ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_services.TryGetValue(key, out var existing))
                return existing as T ?? throw new InvalidOperationException($"Session service '{key}' has another type.");
            var created = factory() ?? throw new InvalidOperationException("Session service factory returned null.");
            _services.Add(key, created); _creationOrder.Add(key);
            return created;
        }
    }

    public bool TryGet<T>(string key, out T? service) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            service = !_disposed && _services.TryGetValue(key, out var found) ? found as T : null;
            return service is not null;
        }
    }

    public ValueTask RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (!_services.Remove(key, out var service)) return ValueTask.CompletedTask;
            _creationOrder.Remove(key);
            if (_services.Values.Any(value => ReferenceEquals(value, service))) return ValueTask.CompletedTask;
            _removals.RemoveAll(task => task.IsCompletedSuccessfully);
            var removal = DisposeOneAsync(service); _removals.Add(removal);
            return new ValueTask(removal);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            var resources = _creationOrder.AsEnumerable().Reverse().Select(k => _services[k])
                .Distinct(ReferenceEqualityComparer.Instance).ToArray();
            _services.Clear(); _creationOrder.Clear();
            var removals = _removals.ToArray(); _removals.Clear();
            _disposeTask = CloseAsync(resources, removals);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CloseAsync(object[] resources, Task[] removals)
    {
        var errors = new List<Exception>();
        try { _lifetime.Cancel(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var resource in resources)
            try { await DisposeOneAsync(resource).ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        foreach (var removal in removals)
            try { await removal.ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex); }
        _lifetime.Dispose();
        if (errors.Count > 0) throw new AggregateException("Session service cleanup failed.", errors);
    }

    private static async Task DisposeOneAsync(object resource)
    {
        if (resource is IAsyncDisposable asyncResource) await asyncResource.DisposeAsync().ConfigureAwait(false);
        else if (resource is IDisposable disposable) disposable.Dispose();
    }

    // Host CloseAsync should await DisposeAsync. The synchronous fallback must not invoke a
    // plugin's asynchronous cleanup under a UI SynchronizationContext and then block that UI.
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
}

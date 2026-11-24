namespace VisionPlatform.Contracts.Nodes;

/// <summary>Explicit runtime/project session scope. Created services belong to the scope, never NodeOutput.</summary>
public interface IExecutionServices
{
    Guid SessionId { get; }
    CancellationToken LifetimeCancellation { get; }
    T GetOrCreate<T>(string key, Func<T> factory) where T : class;
    bool TryGet<T>(string key, out T? service) where T : class;
    ValueTask RemoveAsync(string key);
}

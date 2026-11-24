namespace VisionPlatform.Communication.Serial;

/// <summary>Cancellation initiates asynchronous abort without closing a device on the cancelling UI thread.
/// The observer catches cleanup failures immediately; disposal waits for cleanup and reports them.</summary>
internal sealed class SerialCancellation : IAsyncDisposable
{
    private readonly ISerialTransport _transport;
    private readonly CancellationTokenRegistration _registration;
    private Task _abort = Task.CompletedTask;
    private Exception? _failure;

    public SerialCancellation(ISerialTransport transport, CancellationToken token)
    {
        _transport = transport;
        _registration = token.Register(() => _abort = ObserveAbortAsync());
    }
    private async Task ObserveAbortAsync()
    {
        try { await _transport.AbortAsync().ConfigureAwait(false); }
        catch (Exception error) { _failure = error; }
    }
    public async ValueTask DisposeAsync()
    {
        // Wait until a racing callback has assigned its abort task before observing it.
        await _registration.DisposeAsync().ConfigureAwait(false);
        await _abort.ConfigureAwait(false);
        if (_failure is not null) throw new IOException("Serial cancellation could not close the underlying transport.", _failure);
    }
}

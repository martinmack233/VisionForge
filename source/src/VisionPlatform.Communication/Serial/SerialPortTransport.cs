using System.IO.Ports;
using VisionPlatform.Communication.Framing;

namespace VisionPlatform.Communication.Serial;

/// <summary>System.IO.Ports adapter. Native reads use bounded 50ms polling on a worker, so cancelled
/// reads finish rather than leaving an orphaned ReadAsync behind. Close/dispose also run off the UI.</summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private SerialPort? _port;
    private int _aborted;
    private readonly object _abortGate = new();
    private Task? _abortTask;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _configuredWriteTimeout = 2000;
    public bool IsOpen => Volatile.Read(ref _aborted) == 0 && _port?.IsOpen == true;
    public Stream Stream { get; }
    public SerialPortTransport() => Stream = new PortStream(this);

    public ValueTask OpenAsync(SerialOpenOptions options, CancellationToken cancellationToken) => new(Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _aborted) != 0, this);
        var port = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
        {
            ReadTimeout = 50, WriteTimeout = options.TimeoutMilliseconds, Encoding = FrameCodec.GetEncoding(options.Protocol.Encoding)
        };
        _configuredWriteTimeout = options.TimeoutMilliseconds;
        if (Interlocked.CompareExchange(ref _port, port, null) is not null) { port.Dispose(); throw new InvalidOperationException("Serial transport is already initialized."); }
        try
        {
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _aborted) != 0) throw new OperationCanceledException("Serial open was aborted.");
        }
        catch { Interlocked.CompareExchange(ref _port, null, port); port.Dispose(); throw; }
    }, cancellationToken));

    public ValueTask AbortAsync()
    {
        lock (_abortGate)
        {
            if (_abortTask is not null) return new ValueTask(_abortTask);
            Interlocked.Exchange(ref _aborted, 1);
            var port = Interlocked.Exchange(ref _port, null);
            _abortTask = port is null ? Task.CompletedTask : Task.Run(() =>
            {
                try { if (port.IsOpen) { port.DiscardInBuffer(); port.DiscardOutBuffer(); } }
                finally { port.Dispose(); }
            });
            return new ValueTask(_abortTask);
        }
    }
    public ValueTask DisposeAsync() => AbortAsync();

    private SerialPort GetPort()
    {
        var port = Volatile.Read(ref _port);
        return Volatile.Read(ref _aborted) == 0 && port?.IsOpen == true ? port : throw new IOException("Serial port is closed.");
    }
    private sealed class PortStream(SerialPortTransport owner) : Stream
    {
        public override bool CanRead => owner.IsOpen;
        public override bool CanWrite => owner.IsOpen;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return ValueTask.FromResult(0);
            return new ValueTask<int>(Task.Run(() =>
            {
                var bytes = new byte[Math.Min(buffer.Length, 4096)];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var read = owner.GetPort().Read(bytes, 0, bytes.Length);
                        bytes.AsSpan(0, read).CopyTo(buffer.Span); return read;
                    }
                    catch (TimeoutException) { cancellationToken.ThrowIfCancellationRequested(); }
                }
            }, cancellationToken));
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await owner._writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var port = owner.GetPort();
                // A runtime operation supplies its own deadline token. Do not impose a shorter
                // native timeout: cancellation closes the port to interrupt this single write.
                port.WriteTimeout = cancellationToken.CanBeCanceled ? SerialPort.InfiniteTimeout : owner._configuredWriteTimeout;
                await using var abortOnCancellation = new SerialCancellation(owner, cancellationToken);
                var bytes = buffer.ToArray();
                try
                {
                    await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        port.Write(bytes, 0, bytes.Length);
                    }).ConfigureAwait(false);
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested)
                { throw new OperationCanceledException("Serial write was cancelled; the port was aborted and delivery is uncertain.", error, cancellationToken); }
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally { owner._writeGate.Release(); }
        }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous serial reads.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous serial writes.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

using System.Buffers.Binary;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace VisionPlatform.Communication.Framing;

public enum MessageBoundaryMode { Delimiter, LengthPrefix }
public enum MessageEncoding { Utf8, Ascii, Utf16LittleEndian }
public sealed record FrameProtocolOptions
{
    [DisplayName("消息边界协议")] public MessageBoundaryMode Mode { get; init; } = MessageBoundaryMode.Delimiter;
    [DisplayName("编码")] public MessageEncoding Encoding { get; init; } = MessageEncoding.Utf8;
    [DisplayName("消息分隔符"), VisionPlatform.Contracts.Nodes.AppliesWhen("Mode","Delimiter")] public string Delimiter { get; init; } = "\\n";
    [DisplayName("消息字节上限"), Range(1, 1048576)] public int MaximumMessageBytes { get; init; } = 65536;
}

/// <summary>Transport-independent framing. Length prefix is a four-byte unsigned big-endian byte count.</summary>
public static class FrameCodec
{
    public static IReadOnlyList<string> Validate(FrameProtocolOptions? options)
    {
        if (options is null) return ["Frame protocol parameters are required."];
        var errors = new List<string>();
        if (!Enum.IsDefined(options.Mode)) errors.Add("Message boundary mode is invalid.");
        if (!Enum.IsDefined(options.Encoding)) errors.Add("Message encoding is invalid.");
        if (options.MaximumMessageBytes is < 1 or > 1048576) errors.Add("MaximumMessageBytes must be in [1,1048576].");
        if (options.Mode == MessageBoundaryMode.Delimiter)
            try { var bytes = DelimiterBytes(options); if (bytes.Length is < 1 or > 16) errors.Add("Encoded delimiter must have 1 to 16 bytes."); }
            catch (Exception ex) when (ex is ArgumentException or EncoderFallbackException) { errors.Add("Delimiter cannot be encoded."); }
        return errors;
    }
    public static Encoding GetEncoding(MessageEncoding encoding) => encoding switch
    {
        MessageEncoding.Utf8 => new UTF8Encoding(false, true),
        MessageEncoding.Ascii => System.Text.Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        MessageEncoding.Utf16LittleEndian => new UnicodeEncoding(false, false, true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };
    public static byte[] DelimiterBytes(FrameProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Delimiter);
        var delimiter = options.Delimiter.Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal);
        return GetEncoding(options.Encoding).GetBytes(delimiter);
    }
    public static byte[] Encode(string message, FrameProtocolOptions options)
    {
        var errors = Validate(options); if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        ArgumentNullException.ThrowIfNull(message);
        var encoding = GetEncoding(options.Encoding);
        var length = encoding.GetByteCount(message);
        if (length > options.MaximumMessageBytes) throw new ArgumentException("Encoded message exceeds MaximumMessageBytes.", nameof(message));
        var body = encoding.GetBytes(message);
        if (options.Mode == MessageBoundaryMode.LengthPrefix)
        {
            var result = new byte[checked(body.Length + 4)];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)body.Length); body.CopyTo(result, 4); return result;
        }
        var delimiter = DelimiterBytes(options);
        if (FindDelimiter(body, delimiter, options.Encoding) >= 0)
            throw new ArgumentException("Message contains its delimiter; use length-prefix framing for arbitrary text.", nameof(message));
        var framed = new byte[checked(body.Length + delimiter.Length)]; body.CopyTo(framed, 0); delimiter.CopyTo(framed, body.Length); return framed;
    }
    public static byte[] EncodeBytes(ReadOnlySpan<byte> body,FrameProtocolOptions options)
    {
        var errors=Validate(options);if(errors.Count>0)throw new ArgumentException(string.Join(" ",errors));
        if(options.Mode!=MessageBoundaryMode.LengthPrefix)throw new ArgumentException("受控二进制通信需要4字节大端长度前缀，不猜测分隔符或文本对齐。",nameof(options));
        if(body.Length>options.MaximumMessageBytes)throw new ArgumentException("二进制消息超过配置字节上限。");
        var framed=new byte[body.Length+4];BinaryPrimitives.WriteUInt32BigEndian(framed,(uint)body.Length);body.CopyTo(framed.AsSpan(4));return framed;
    }
    internal static int FindDelimiter(ReadOnlySpan<byte> source, ReadOnlySpan<byte> delimiter, MessageEncoding encoding)
    {
        var start = 0;
        while (start <= source.Length - delimiter.Length)
        {
            var offset = source[start..].IndexOf(delimiter); if (offset < 0) return -1;
            var index = start + offset;
            if (encoding != MessageEncoding.Utf16LittleEndian || index % 2 == 0) return index;
            start = index + 1;
        }
        return -1;
    }
}

/// <summary>Stateful bounded frame reader. A caller serializes reads and owns the underlying stream.</summary>
public sealed class FramedMessageReader
{
    private readonly Stream _stream;
    private readonly FrameProtocolOptions _options;
    private readonly MemoryStream _pending = new();
    public FramedMessageReader(Stream stream, FrameProtocolOptions options)
    {
        var errors = FrameCodec.Validate(options); if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        _stream = stream; _options = options;
    }
    public async Task<string> ReadAsync(CancellationToken cancellationToken = default) => FrameCodec.GetEncoding(_options.Encoding).GetString(await ReadBytesAsync(cancellationToken).ConfigureAwait(false));
    public async Task<byte[]> ReadBytesAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Mode == MessageBoundaryMode.LengthPrefix)
        {
            var prefix = new byte[4]; await ReadExactly(prefix, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length > _options.MaximumMessageBytes) throw new InvalidDataException("Remote length prefix exceeds MaximumMessageBytes.");
            var body = new byte[(int)length]; await ReadExactly(body, cancellationToken).ConfigureAwait(false);
            return body;
        }
        var delimiter = FrameCodec.DelimiterBytes(_options); var chunk = new byte[Math.Min(4096, _options.MaximumMessageBytes + delimiter.Length)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = TakePending(delimiter);
            if (result is not null) return result;
            if (_pending.Length > _options.MaximumMessageBytes + delimiter.Length - 1)
                throw new InvalidDataException("Remote message exceeds MaximumMessageBytes before delimiter.");
            var read = await _stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Remote endpoint disconnected before a complete message.");
            _pending.Write(chunk, 0, read);
        }
    }
    private byte[]? TakePending(byte[] delimiter)
    {
        var buffer = _pending.GetBuffer().AsSpan(0, (int)_pending.Length);
        var index = FrameCodec.FindDelimiter(buffer, delimiter, _options.Encoding);
        if (index < 0) return null;
        if (index > _options.MaximumMessageBytes) throw new InvalidDataException("Remote message exceeds MaximumMessageBytes.");
        var message = buffer[..index].ToArray();
        var remaining = buffer[(index + delimiter.Length)..].ToArray();
        _pending.SetLength(0); _pending.Write(remaining); return message;
    }
    private async Task ReadExactly(byte[] bytes, CancellationToken ct)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await _stream.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Remote endpoint disconnected during a length-prefixed message.");
            read += count;
        }
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using VisionPlatform.Communication.Framing;

namespace VisionPlatform.Communication.Serial;

public sealed record SerialOpenOptions
{
    [DisplayName("连接标识")] public string ConnectionId { get; init; } = "serial";
    [DisplayName("COM 端口")] public string PortName { get; init; } = "COM1";
    [DisplayName("波特率"), Range(1, 4000000)] public int BaudRate { get; init; } = 9600;
    [DisplayName("数据位"), Range(5, 8)] public int DataBits { get; init; } = 8;
    [DisplayName("校验位")] public Parity Parity { get; init; } = Parity.None;
    [DisplayName("停止位")] public StopBits StopBits { get; init; } = StopBits.One;
    [DisplayName("打开超时（ms）"), Range(1, 120000)] public int TimeoutMilliseconds { get; init; } = 2000;
    [DisplayName("编码与消息边界")] public FrameProtocolOptions Protocol { get; init; } = new();
}

public sealed record SerialPortReference(Guid SessionId, string Id);

/// <summary>Injectable byte transport. Abort/dispose must interrupt pending IO; callers own no hardware handle.</summary>
public interface ISerialTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Stream Stream { get; }
    ValueTask OpenAsync(SerialOpenOptions options, CancellationToken cancellationToken);
    ValueTask AbortAsync();
}

using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using VisionPlatform.Communication.Serial;

namespace VisionPlatform.Communication.Modbus;
public enum ModbusTransport{[Display(Name="Modbus TCP客户端")]Tcp,[Display(Name="Modbus RTU主站（真实硬件待验）")]Rtu}
public enum ModbusOperation
{
    [Display(Name="明确建立连接")]Connect,[Display(Name="关闭显式连接")]Close,
    [Display(Name="01 读线圈")]ReadCoils,[Display(Name="02 读离散输入")]ReadDiscreteInputs,
    [Display(Name="03 读保持寄存器")]ReadHoldingRegisters,[Display(Name="04 读输入寄存器")]ReadInputRegisters,
    [Display(Name="05 写单线圈（副作用）")]WriteSingleCoil,[Display(Name="06 写单寄存器（副作用）")]WriteSingleRegister,
    [Display(Name="15 写多个线圈（副作用）")]WriteMultipleCoils,[Display(Name="16 写多个寄存器（副作用）")]WriteMultipleRegisters
}
public sealed record ModbusConnectionReference(Guid SessionId,string Id,ModbusTransport Transport)
{ public Guid Generation{get;init;}=Guid.NewGuid(); }
public sealed record ModbusTcpOptions
{
    [DisplayName("明确目标IP（不解析/搜索地址）")]public string Host{get;init;}="127.0.0.1";
    [DisplayName("TCP端口"),Range(1,65535)]public int Port{get;init;}=502;
}
public sealed record ModbusSerialOptions
{
    [DisplayName("COM端口")]public string PortName{get;init;}="COM1";
    [DisplayName("波特率"),Range(1,4000000)]public int BaudRate{get;init;}=9600;
    [DisplayName("数据位（RTU固定8）"),Range(8,8)]public int DataBits{get;init;}=8;
    [DisplayName("校验位")]public System.IO.Ports.Parity Parity{get;init;}=System.IO.Ports.Parity.None;
    [DisplayName("停止位")]public System.IO.Ports.StopBits StopBits{get;init;}=System.IO.Ports.StopBits.One;
    public SerialOpenOptions ToSerial(string id,int timeout)=>new(){ConnectionId=id,PortName=PortName,BaudRate=BaudRate,DataBits=DataBits,Parity=Parity,StopBits=StopBits,TimeoutMilliseconds=timeout};
}
public sealed record ModbusEvent(DateTimeOffset Time,string ConnectionId,ModbusOperation Operation,string State,double Milliseconds,byte? ExceptionCode=null);
public sealed record ModbusConnectionOptions
{
    [DisplayName("连接名称")]public string ConnectionId{get;init;}="modbus";
    public ModbusTransport Transport{get;init;}
    public ModbusTcpOptions Tcp{get;init;}=new();
    public SerialOpenOptions Serial{get;init;}=new();
    [DisplayName("超时 ms"),Range(1,120000)]public int TimeoutMilliseconds{get;init;}=2000;
}
public sealed class ModbusBitValues:IReadOnlyList<bool>
{
    private readonly ReadOnlyCollection<bool> _values;public int Count=>_values.Count;public bool this[int index]=>_values[index];
    public ModbusBitValues(IEnumerable<bool> values){var a=values.Take(2001).ToArray();if(a.Length>2000)throw new ArgumentException("线圈集合最多2000项。");_values=Array.AsReadOnly(a);}
    public IEnumerator<bool> GetEnumerator()=>_values.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public override string ToString()=>$"线圈/离散输入 {Count}项（只读Boolean）";
}
public sealed class ModbusRegisterValues:IReadOnlyList<ushort>
{
    private readonly ReadOnlyCollection<ushort> _values;public int Count=>_values.Count;public ushort this[int index]=>_values[index];
    public ModbusRegisterValues(IEnumerable<ushort> values){var a=values.Take(126).ToArray();if(a.Length>125)throw new ArgumentException("原始寄存器集合最多125项。");_values=Array.AsReadOnly(a);}
    public IEnumerator<ushort> GetEnumerator()=>_values.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public override string ToString()=>$"原始UInt16寄存器 {Count}项（只读；解码需显式字节/字序）";
}
public sealed record ModbusResult(Guid RequestId,ModbusOperation Operation,byte UnitId,int Address,ushort? TransactionId,
    ModbusBitValues Coils,ModbusRegisterValues Registers,double Milliseconds,bool ResponseReceived,bool DuplicateSuppressed=false);
public sealed record ModbusRequest(ModbusOperation Operation,byte UnitId,int Address,int Quantity=1,bool CoilValue=false,ushort RegisterValue=0,
    ModbusBitValues? Coils=null,ModbusRegisterValues? Registers=null);
public sealed class ModbusProtocolException(string message,byte functionCode,byte? exceptionCode=null):IOException(message)
{public byte FunctionCode{get;}=functionCode;public byte? ExceptionCode{get;}=exceptionCode;}
public sealed class ModbusWriteUncertainException(string message,Exception inner):IOException(message,inner);
public enum ModbusDataType{[Display(Name="UInt16 无符号16位")]UInt16,[Display(Name="Int16 有符号16位")]Int16,[Display(Name="UInt32 无符号32位")]UInt32,[Display(Name="Int32 有符号32位")]Int32,[Display(Name="Float32 IEEE754")]Float32}
public enum RegisterByteOrder{[Display(Name="每寄存器高字节优先（协议原始顺序）")]BigEndian,[Display(Name="交换每寄存器字节")]LittleEndian}
public enum RegisterWordOrder{[Display(Name="高16位字在前")]HighWordFirst,[Display(Name="低16位字在前")]LowWordFirst}
public static class ModbusRegisterDecoder
{
    public static double[] Decode(ModbusRegisterValues registers,ModbusDataType type,RegisterByteOrder bytes,RegisterWordOrder words,int start,int count)
    {
        if(!Enum.IsDefined(type)||!Enum.IsDefined(bytes)||!Enum.IsDefined(words)||start<0||count is <1 or >125)throw new ArgumentException("寄存器解码类型、字节/字序或范围无效。");
        var width=type is ModbusDataType.UInt16 or ModbusDataType.Int16?1:2;if((long)start+count*width>registers.Count)throw new ArgumentException("解码范围超过实际寄存器数量（索引从0开始）。");var result=new double[count];
        ushort Word(int i){var v=registers[i];return bytes==RegisterByteOrder.BigEndian?v:(ushort)((v>>8)|(v<<8));}
        for(var i=0;i<count;i++){var at=start+i*width;var a=Word(at);if(width==1)result[i]=type==ModbusDataType.Int16?unchecked((short)a):a;else{var b=Word(at+1);uint bits=words==RegisterWordOrder.HighWordFirst?((uint)a<<16)|b:((uint)b<<16)|a;result[i]=type switch{ModbusDataType.UInt32=>bits,ModbusDataType.Int32=>unchecked((int)bits),_=>BitConverter.Int32BitsToSingle(unchecked((int)bits))};if(!double.IsFinite(result[i]))throw new ArgumentException("Float32寄存器解码为NaN/Infinity；未填0或猜测其他字序。");}}
        return result;
    }
}

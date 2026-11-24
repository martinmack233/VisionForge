using System.Buffers.Binary;

namespace VisionPlatform.Communication.Modbus;
/// <summary>Own implementation from public Modbus v1.1b3 / serial v1.02 wire contracts. No external DLL.</summary>
public static class ModbusCodec
{
    public static byte Function(ModbusOperation operation)=>operation switch{ModbusOperation.ReadCoils=>1,ModbusOperation.ReadDiscreteInputs=>2,ModbusOperation.ReadHoldingRegisters=>3,ModbusOperation.ReadInputRegisters=>4,ModbusOperation.WriteSingleCoil=>5,ModbusOperation.WriteSingleRegister=>6,ModbusOperation.WriteMultipleCoils=>15,ModbusOperation.WriteMultipleRegisters=>16,_=>throw new ArgumentException("此操作没有数据功能码。")};
    public static bool IsWrite(ModbusOperation op)=>op is ModbusOperation.WriteSingleCoil or ModbusOperation.WriteSingleRegister or ModbusOperation.WriteMultipleCoils or ModbusOperation.WriteMultipleRegisters;
    public static byte[] RequestPdu(ModbusRequest request,ModbusTransport transport)
    {
        var fc=Function(request.Operation);if(!Enum.IsDefined(transport)||transport==ModbusTransport.Rtu&&request.UnitId is <1 or >247)throw new ArgumentException("RTU从站地址必须为1..247，首版不执行广播写。");
        var quantity=fc switch{5 or 6=>1,15=>request.Coils?.Count??0,16=>request.Registers?.Count??0,_=>request.Quantity};var maximum=fc switch{1 or 2=>2000,3 or 4=>125,15=>1968,16=>123,_=>1};
        if(request.Address is <0 or >65535||quantity<1||quantity>maximum||(long)request.Address+quantity>65536)throw new ArgumentException($"Modbus使用0基地址0..65535；此功能数量为1..{maximum}，地址+数量不能越界。");
        var payload=fc==15?(quantity+7)/8:fc==16?quantity*2:0;var pdu=new byte[payload>0?6+payload:5];pdu[0]=fc;BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1),(ushort)request.Address);
        var value=fc==5?(request.CoilValue?0xff00:0):fc==6?request.RegisterValue:quantity;BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3),(ushort)value);
        if(payload>0){pdu[5]=(byte)payload;for(var i=0;i<quantity;i++){if(fc==15){if(request.Coils![i])pdu[6+i/8]|=(byte)(1<<(i%8));}else BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6+i*2),request.Registers![i]);}}return pdu;
    }
    public static (ModbusBitValues Coils,ModbusRegisterValues Registers) Response(ModbusRequest request,ReadOnlySpan<byte> pdu)
    {
        var fc=Function(request.Operation);if(pdu.Length>=1&&pdu[0]==(fc|0x80)){if(pdu.Length!=2)throw new ModbusProtocolException("Modbus异常响应长度错误。",fc);throw new ModbusProtocolException($"Modbus功能码{fc}异常响应0x{pdu[1]:X2}：{ExceptionLabel(pdu[1])}",fc,pdu[1]);}
        if(pdu.Length<2||pdu[0]!=fc)throw new ModbusProtocolException("Modbus响应功能码不匹配。",fc);
        if(fc is 1 or 2){var count=(request.Quantity+7)/8;if(pdu[1]!=count||pdu.Length!=count+2)throw new ModbusProtocolException("线圈响应字节数或帧长度不匹配。",fc);if(request.Quantity%8!=0&&(pdu[^1]>>(request.Quantity%8))!=0)throw new ModbusProtocolException("线圈响应末字节高位填充不是0。",fc);var bits=new bool[request.Quantity];for(var i=0;i<bits.Length;i++)bits[i]=(pdu[2+i/8]&(1<<(i%8)))!=0;return(new(bits),new([]));}
        if(fc is 3 or 4){if(pdu[1]!=request.Quantity*2||pdu.Length!=2+request.Quantity*2)throw new ModbusProtocolException("寄存器响应字节数或帧长度不匹配。",fc);var values=new ushort[request.Quantity];for(var i=0;i<values.Length;i++)values[i]=BinaryPrimitives.ReadUInt16BigEndian(pdu.Slice(2+i*2));return(new([]),new(values));}
        var expected=RequestPdu(request,ModbusTransport.Tcp);if(pdu.Length!=5||!pdu.SequenceEqual(expected.AsSpan(0,5)))throw new ModbusProtocolException("写入响应的地址/值/数量回显不匹配；写入状态不应当作已确认。",fc);return(new([]),new([]));
    }
    public static string ExceptionLabel(byte code)=>code switch{1=>"不支持的功能码",2=>"非法数据地址",3=>"非法数据值",4=>"设备执行故障",5=>"设备已确认请求但尚未完成",6=>"设备忙",8=>"内存奇偶错误",10=>"网关路径不可用",11=>"网关目标无响应",_=>"设备返回的异常码，请核对设备手册"};
    public static ushort Crc16(ReadOnlySpan<byte> data){ushort crc=0xffff;foreach(var value in data){crc^=value;for(var i=0;i<8;i++)crc=(ushort)((crc&1)!=0?(crc>>1)^0xa001:crc>>1);}return crc;}
    public static byte[] RtuFrame(byte unit,ReadOnlySpan<byte> pdu){if(pdu.Length is <1 or >253)throw new ArgumentException("RTU PDU长度无效。");var frame=new byte[pdu.Length+3];frame[0]=unit;pdu.CopyTo(frame.AsSpan(1));BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length-2),Crc16(frame.AsSpan(0,frame.Length-2)));return frame;}
    public static byte[] CheckRtu(byte unit,byte function,ReadOnlySpan<byte> frame){if(frame.Length is <5 or >256||frame[0]!=unit||BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..])!=Crc16(frame[..^2]))throw new ModbusProtocolException("RTU地址、长度或CRC不匹配；连接已失效，不复用迟到帧。",function);return frame[1..^2].ToArray();}
}

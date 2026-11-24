using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionPlatform.Algorithms.Utilities;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;
public enum RandomNumberKind
{
    [Display(Name="32位整数",Description="下界包含、上界不包含；Int32范围，转为double输出时仍精确。") ]Integer,
    [Display(Name="浮点数",Description="IEEE754 double；下界包含、上界不包含。不是密码学随机数。") ]Real
}
public enum RandomOutputMode
{
    [Display(Name="单值",Description="Value输出有效，同时保留一项强类型集合。") ]Single,
    [Display(Name="集合",Description="仅显式集合输出，不将第一项偷偷作为Value；可接集合选择/逐项流程。") ]Collection
}
public sealed record RandomNumberOptions
{
    [DisplayName("数值类型")]public RandomNumberKind Kind{get;init;}
    [DisplayName("输出方式")]public RandomOutputMode Mode{get;init;}
    [DisplayName("整数下界（包含）"),AppliesWhen("Kind","Integer")]public int MinimumInteger{get;init;}
    [DisplayName("整数上界（不包含）"),AppliesWhen("Kind","Integer")]public int MaximumInteger{get;init;}=100;
    [DisplayName("浮点下界（包含）"),Description("有限double，下界必须小于上界且差值有限。"),AppliesWhen("Kind","Real")]
    public double Minimum{get;init;}
    [DisplayName("浮点上界（不包含）"),AppliesWhen("Kind","Real")]public double Maximum{get;init;}=1;
    [DisplayName("集合数量"),Range(1,10000),AppliesWhen("Mode","Collection")]public int Count{get;init;}=10;
    [DisplayName("固定种子（可选）"),Description("相同.NET版本、节点ID、参数、种子及迭代索引复现。每Runtime会话独立；同会话重复执行会推进对应索引的序列。并发调用按锁获得顺序分配，不保证线程到达顺序。")]
    public int? Seed{get;init;}
}
[SubflowSafe("IterationIndex")]
public sealed class RandomNumberNode:NodeBase<RandomNumberOptions>
{
    private sealed class StreamState
    {
        private readonly object _gate=new();private readonly Dictionary<int,(Random Random,long Sequence)> _streams=[];private string _identity="";private int _seed;
        public (double[] Values,long Sequence) Next(RandomNumberOptions p,string identity,string nodeId,int index,int count,CancellationToken ct)
        {
            lock(_gate)
            {
                if(_identity!=identity){_identity=identity;_streams.Clear();_seed=p.Seed??RandomNumberGenerator.GetInt32(int.MinValue,int.MaxValue);}
                if(!_streams.TryGetValue(index,out var entry))
                {
                    if(_streams.Count>=128)throw new InvalidOperationException("此随机算子已达到128条索引随机流预算；请开始新的明确Runtime会话。");
                    var seedBytes=SHA256.HashData(Encoding.UTF8.GetBytes(_seed.ToString(CultureInfo.InvariantCulture)+"\n"+nodeId+"\n"+identity+"\n"+index.ToString(CultureInfo.InvariantCulture)));entry=(new Random(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(seedBytes)),0);
                }
                var values=new double[count];for(var i=0;i<count;i++)
                {
                    ct.ThrowIfCancellationRequested();if(p.Kind==RandomNumberKind.Integer)values[i]=entry.Random.NextInt64(p.MinimumInteger,p.MaximumInteger);
                    else{var v=p.Minimum+(p.Maximum-p.Minimum)*entry.Random.NextDouble();values[i]=v>=p.Maximum?Math.BitDecrement(p.Maximum):v;}
                }
                var sequence=checked(entry.Sequence+1);_streams[index]=(entry.Random,sequence);return(values,sequence);
            }
        }
    }
    public override NodeDescriptor Descriptor=>Describe("RandomNumber",[new("IterationIndex",typeof(int),false)],
        [new("Value",typeof(double),false),new("Collection",typeof(VisionCollection)),new("Count",typeof(int)),new("Sequence",typeof(long))]);
    protected override IReadOnlyList<string> Validate(RandomNumberOptions p)
    {
        if(!Enum.IsDefined(p.Kind)||!Enum.IsDefined(p.Mode))return["随机数类型或输出方式无效。"];
        if(p.Mode==RandomOutputMode.Collection&&p.Count is <1 or >10000)return["集合数量必须为1..10000。"];
        if(p.Kind==RandomNumberKind.Integer&&p.MinimumInteger>=p.MaximumInteger)return["整数下界必须小于上界；下界包含，上界不包含。"];
        if(p.Kind==RandomNumberKind.Real&&(!double.IsFinite(p.Minimum)||!double.IsFinite(p.Maximum)||p.Minimum>=p.Maximum||!double.IsFinite(p.Maximum-p.Minimum)))return["浮点范围必须有限且严格递增，差值不能溢出。"];
        return[];
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        var p=c.Parameters<RandomNumberOptions>();var errors=Validate(p);if(errors.Count>0)throw new ArgumentException(string.Join(";",errors));var index=c.TryInput<int>("IterationIndex",out var supplied)?supplied:-1;if(index is <-1 or >9999||c.Inputs.ContainsKey("IterationIndex")&&index<0)throw new ArgumentException("迭代索引必须为0..9999；未接索引时使用独立普通随机流。");
        var services=c.Services??throw new InvalidOperationException("随机数需要现有Runtime会话管理状态，不使用全局Random。");var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p,JsonDefaults.Options))));var state=services.GetOrCreate("vision.random."+c.Spec.NodeId,()=>new StreamState());
        var generated=state.Next(p,identity,c.Spec.NodeId,index,p.Mode==RandomOutputMode.Single?1:p.Count,ct);var values=generated.Values;var collection=new VisionCollection(VisionItemKind.Scalar,values.Select((v,i)=>new ScalarItem(new(c.RunId,c.Spec.NodeId,i,null,"scalar",$"{c.RunId:N}/{c.Spec.NodeId}/{i}"),v)));
        var output=new Dictionary<string,object>{{"Collection",collection},{"Count",values.Length},{"Sequence",generated.Sequence}};if(p.Mode==RandomOutputMode.Single)output["Value"]=values[0];return ValueTask.FromResult(new NodeOutput(output));
    }
}
[SubflowSafe]
public sealed class FileReadNode:NodeBase<FileReadOptions>
{
    public override NodeDescriptor Descriptor=>Describe("FileRead",[],[new("Result",typeof(FileReadResult)),new("Text",typeof(string),false),new("Bytes",typeof(BinaryData),false)]);
    protected override IReadOnlyList<string> Validate(FileReadOptions p)=>LocalFileReader.Validate(p);
    public override async ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {var result=await LocalFileReader.ReadAsync(c.Parameters<FileReadOptions>(),c.ResourceRoot,ct).ConfigureAwait(false);var values=new Dictionary<string,object>{{"Result",result}};if(result.Text is{}text)values["Text"]=text;if(result.Bytes is{}bytes)values["Bytes"]=bytes;return new(values);}
}
public enum FileValueSelection
{
    [Display(Name="全文",Description="来自文本/JSON/CSV的原始解码文本；保留换行，开头BOM已移除。") ]WholeText,
    [Display(Name="JSON指针",Description="RFC6901：空指针为根，/分层；~0表示~、~1表示/，不执行表达式。") ]JsonPointer,
    [Display(Name="CSV单元格",Description="数据行和列从0开始，表头不计入数据行。") ]CsvCell,
    [Display(Name="二进制单字节",Description="0基字节索引，得到UInt8数值或十进制文字；不自动解码寄存器。") ]BinaryByte,
    [Display(Name="二进制片段",Description="0基偏移及明确长度，输出不可变字节集合。") ]BinarySlice
}
public enum FileValueTarget
{
    [Display(Name="文字",Description="JSON非字符串值保留原始JSON字面量；字符串保留实际内容。") ]String,
    [Display(Name="double数值",Description="显式转换为有限IEEE754 double；小数按double舍入，超过2^53的整数拒绝。原JSON文本仍保留完整精度。") ]Number,
    [Display(Name="Boolean",Description="JSON必须为Boolean；文本/CSV仅明确true或false，不将缺失或其他文字当false。") ]Boolean,
    [Display(Name="二进制字节",Description="仅用于二进制片段，不隐式执行Base64解码或重编码文本。") ]Bytes
}
public sealed record FileValueOptions
{
    [DisplayName("选择方式")]public FileValueSelection Selection{get;init;}
    [DisplayName("输出类型")]public FileValueTarget Target{get;init;}
    [DisplayName("JSON指针"),Description("例如 /items/0/name；空字符串选择根。不支持任意代码、查询表达式或URI片段。"),AppliesWhen("Selection","JsonPointer")]
    public string Pointer{get;init;}="";
    [DisplayName("数据行（0基）"),Range(0,99999),AppliesWhen("Selection","CsvCell")]public int Row{get;init;}
    [DisplayName("列（0基）"),Range(0,1023),AppliesWhen("Selection","CsvCell")]public int Column{get;init;}
    [DisplayName("字节偏移（0基）"),Range(0,16777216),AppliesWhen("Selection","BinaryByte","BinarySlice")]public int Offset{get;init;}
    [DisplayName("片段字节数"),Range(0,16777216),AppliesWhen("Selection","BinarySlice")]public int Length{get;init;}=1;
    [DisplayName("最大返回文字字符数"),Range(1,1048576)]public int MaximumCharacters{get;init;}=65536;
}
[SubflowSafe]
public sealed class FileValueNode:NodeBase<FileValueOptions>
{
    public override NodeDescriptor Descriptor=>Describe("FileValue",[new("Result",typeof(FileReadResult))],
        [new("Text",typeof(string),false),new("Number",typeof(double),false),new("Boolean",typeof(bool),false),new("Bytes",typeof(BinaryData),false)]);
    protected override IReadOnlyList<string> Validate(FileValueOptions p)
    {
        if(!Enum.IsDefined(p.Selection)||!Enum.IsDefined(p.Target)||p.MaximumCharacters is <1 or >1048576)return["文件字段选择、输出类型或返回字符预算无效。"];
        if(p.Selection==FileValueSelection.JsonPointer&&(p.Pointer is null||p.Pointer.Length>4096||p.Pointer.Length>0&&!p.Pointer.StartsWith('/')))return["JSON指针必须为空或以/开始，最长4096字符；不支持表达式。"];
        if(p.Selection==FileValueSelection.CsvCell&&(p.Row is <0 or >99999||p.Column is <0 or >1023))return["CSV数据行/列必须是合法0基索引。"];
        if(p.Selection is FileValueSelection.BinaryByte or FileValueSelection.BinarySlice&&p.Offset is <0 or >16777216||p.Selection==FileValueSelection.BinarySlice&&p.Length is <0 or >16777216)return["二进制偏移及长度必须位于0..16777216。"];
        if(p.Selection==FileValueSelection.BinarySlice?p.Target!=FileValueTarget.Bytes:p.Target==FileValueTarget.Bytes)return["仅二进制片段可输出Bytes；片段必须选择Bytes输出类型。"];
        if(p.Selection==FileValueSelection.BinaryByte&&p.Target==FileValueTarget.Boolean)return["二进制字节不隐式解释为Boolean，请明确解码。"];return[];
    }
    private static JsonElement Pointer(JsonElement root,string path)
    {
        var current=root;if(path.Length==0)return current;
        foreach(var raw in path.Split('/').Skip(1))
        {
            for(var i=0;i<raw.Length;i++)if(raw[i]=='~'&&(i+1>=raw.Length||raw[++i] is not('0' or '1')))throw new ArgumentException("JSON指针仅允许~0及~1转义。");
            var part=raw.Replace("~1","/",StringComparison.Ordinal).Replace("~0","~",StringComparison.Ordinal);
            if(current.ValueKind==JsonValueKind.Object){if(!current.TryGetProperty(part,out current))throw new KeyNotFoundException("JSON指针属性不存在："+part);}
            else if(current.ValueKind==JsonValueKind.Array){if(part.Length==0||part.Length>1&&part[0]=='0'||!int.TryParse(part,NumberStyles.None,CultureInfo.InvariantCulture,out var index)||index<0||index>=current.GetArrayLength())throw new ArgumentOutOfRangeException(nameof(path),"JSON数组需要存在的0基索引，不允许前导零或-。");current=current[index];}
            else throw new ArgumentException("JSON指针试图继续访问标量值。");
        }
        return current;
    }
    private static double Number(string text)
    {
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)||!double.IsFinite(number))throw new FormatException("字段不是有限的不变区域数值。");
        if(decimal.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var precise)&&decimal.Truncate(precise)==precise&&Math.Abs(precise)>9007199254740992m||Math.Truncate(number)==number&&Math.Abs(number)>9007199254740992d)throw new FormatException("整数超过double的2^53精确范围；请以文字查看以保留原值。");
        var exponent=text.IndexOfAny(['e','E']);var mantissa=exponent<0?text:text[..exponent];if(number==0&&mantissa.Any(c=>c is >='1' and <='9'))throw new FormatException("非零数值超出double可表示的小量范围，不能静默归零。");return number;
    }
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var p=c.Parameters<FileValueOptions>();var errors=Validate(p);if(errors.Count>0)throw new ArgumentException(string.Join(";",errors));var source=c.Input<FileReadResult>("Result");string? text=null;JsonElement? json=null;
        switch(p.Selection)
        {
            case FileValueSelection.WholeText:text=source.Text??throw new ArgumentException("此文件快照没有文本，请选择正确读取格式。");break;
            case FileValueSelection.JsonPointer:json=Pointer(source.Json??throw new ArgumentException("需要JSON读取结果，不能将任意文本当JSON字段。"),p.Pointer);break;
            case FileValueSelection.CsvCell:text=(source.Table??throw new ArgumentException("需要CSV读取结果。")).Cell(p.Row,p.Column);break;
            case FileValueSelection.BinaryByte:var data=source.Bytes??throw new ArgumentException("需要二进制读取结果。");if(p.Offset>=data.Count)throw new ArgumentOutOfRangeException(nameof(p.Offset),"字节索引越界，未返回默认0。");text=data[p.Offset].ToString(CultureInfo.InvariantCulture);break;
            case FileValueSelection.BinarySlice:var bytes=source.Bytes??throw new ArgumentException("需要二进制读取结果。");if(p.Offset>bytes.Count-p.Length)throw new ArgumentOutOfRangeException(nameof(p.Length),"二进制片段超出文件范围。");ct.ThrowIfCancellationRequested();return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"Bytes",new BinaryData(Enumerable.Range(p.Offset,p.Length).Select(i=>bytes[i]))}}));
        }
        if(json is{}value)
        {
            if(p.Target==FileValueTarget.Number&&value.ValueKind!=JsonValueKind.Number)throw new FormatException("JSON字段不是Number类型；不隐式转换字符串。");
            if(p.Target==FileValueTarget.Boolean&&value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new FormatException("JSON字段不是Boolean类型；不将其他值当false。");
            text=value.ValueKind==JsonValueKind.String?value.GetString():value.GetRawText();
        }
        text??="";if(text.Length>p.MaximumCharacters)throw new InvalidOperationException("字段文字超过返回预算；未截断原始数据。");ct.ThrowIfCancellationRequested();
        var result=new Dictionary<string,object>();switch(p.Target)
        {
            case FileValueTarget.String:result["Text"]=text;break;
            case FileValueTarget.Number:result["Number"]=Number(text);break;
            case FileValueTarget.Boolean:if(!bool.TryParse(text.Trim(),out var boolean))throw new FormatException("字段必须为明确true或false；缺失/其他文本不会被当false。");result["Boolean"]=boolean;break;
            default:throw new ArgumentException("输出类型不适用于所选字段。");
        }
        return ValueTask.FromResult(new NodeOutput(result));
    }
}

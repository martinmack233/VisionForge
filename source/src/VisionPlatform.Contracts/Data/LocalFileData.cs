using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace VisionPlatform.Contracts.Data;
public enum LocalFileFormat
{
    [Display(Name="文本",Description="严格按所选Unicode编码读取；移除开头BOM，保留原始换行。") ]Text,
    [Display(Name="JSON",Description="标准JSON解析；不执行内容，字段使用RFC6901指针显式选择。") ]Json,
    [Display(Name="CSV",Description="解析引号、双引号转义、引号内换行；行列从0开始。") ]Csv,
    [Display(Name="二进制",Description="返回有界、不可变的原始字节；不猜测数据格式。") ]Binary
}
/// <summary>Immutable parsed CSV data. Rows exclude the optional header, indexes start at zero.</summary>
public sealed class TabularData
{
    public IReadOnlyList<string> Headers{get;}
    public IReadOnlyList<IReadOnlyList<string>> Rows{get;}
    public int RowCount=>Rows.Count;
    public int ColumnCount{get;}
    public TabularData(IEnumerable<string>? headers,IEnumerable<IEnumerable<string>> rows)
    {
        var names=(headers??[]).Take(1025).ToArray();if(names.Length>1024)throw new ArgumentException("CSV表头超过1024列。");
        var materialized=rows.Take(100001).Select(row=>(IReadOnlyList<string>)Array.AsReadOnly(row.Take(1025).ToArray())).ToArray();
        if(materialized.Length>100000||materialized.Any(row=>row.Count>1024)||materialized.Sum(row=>(long)row.Count)>1_000_000)throw new ArgumentException("CSV表格超过行、列或100万个单元格的预算。");
        Headers=Array.AsReadOnly(names);Rows=Array.AsReadOnly(materialized);ColumnCount=Math.Max(names.Length,materialized.Select(row=>row.Count).DefaultIfEmpty(0).Max());
    }
    public string Cell(int row,int column)=>row>=0&&row<RowCount&&column>=0&&column<Rows[row].Count?Rows[row][column]:throw new ArgumentOutOfRangeException(nameof(row),"CSV行列索引从0开始；当前单元格不存在。");
    public override string ToString()=>$"CSV {RowCount}行 × {ColumnCount}列，表头{Headers.Count}列（只读）";
}
/// <summary>One read snapshot. No file handle, native buffer, database connection or executable content.</summary>
public sealed class FileReadResult
{
    public string Path{get;}public string Sha256{get;}public LocalFileFormat Format{get;}public string Encoding{get;}public long ByteCount{get;}
    public string? Text{get;}public JsonElement? Json{get;}public TabularData? Table{get;}public BinaryData? Bytes{get;}
    public FileReadResult(string path,string sha256,LocalFileFormat format,string encoding,long byteCount,string? text=null,JsonElement? json=null,TabularData? table=null,BinaryData? bytes=null)
    {if(!Enum.IsDefined(format)||byteCount<0||byteCount>BinaryData.MaximumLength)throw new ArgumentException("文件快照格式或长度无效。");Path=path;Sha256=sha256;Format=format;Encoding=encoding;ByteCount=byteCount;Text=text;Json=json?.Clone();Table=table;Bytes=bytes;}
    public override string ToString()=>$"{Format} {ByteCount} bytes · SHA256 {Sha256}（本次只读快照）";
}

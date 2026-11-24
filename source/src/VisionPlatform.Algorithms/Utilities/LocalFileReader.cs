using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Algorithms.Utilities;
public enum LocalTextEncoding
{
    [Display(Name="UTF-8",Description="严格UTF-8；非法字节报错，不替换成问号。") ]Utf8,
    [Display(Name="UTF-16小端",Description="严格UTF-16LE；无BOM时按此选择解释。") ]Utf16LE,
    [Display(Name="UTF-16大端",Description="严格UTF-16BE；无BOM时按此选择解释。") ]Utf16BE
}
public enum BomHandling
{
    [Display(Name="识别Unicode BOM",Description="BOM优先于编码选择；支持UTF8/UTF16LE/BE，UTF32明确拒绝。") ]Detect,
    [Display(Name="要求与所选编码一致",Description="有BOM时必须与所选编码一致；无BOM仍使用所选编码。") ]RequireSelected
}
public enum CsvSeparator
{
    [Display(Name="逗号 ,") ]Comma,
    [Display(Name="分号 ;") ]Semicolon,
    [Display(Name="制表符 Tab") ]Tab
}
public sealed record FileReadOptions
{
    [DisplayName("本地文件路径"),Description("工程相对路径必须留在工程根内；也可明确指定本地绝对路径。不搜索磁盘，拒绝网络路径、重解析链接及常见密钥/凭据位置。")]
    public string Path{get;init;}="";
    [DisplayName("读取格式")]public LocalFileFormat Format{get;init;}
    [DisplayName("文本编码"),AppliesWhen("Format","Text","Json","Csv")]public LocalTextEncoding Encoding{get;init;}=LocalTextEncoding.Utf8;
    [DisplayName("BOM处理"),AppliesWhen("Format","Text","Json","Csv")]public BomHandling Bom{get;init;}=BomHandling.Detect;
    [DisplayName("最大文件字节数"),Range(1,16777216),Description("默认1MiB，上限16MiB；超过预算直接失败，不截断成看似完整的数据。")]
    public int MaximumBytes{get;init;}=1024*1024;
    [DisplayName("最大文本字符数"),Range(1,16777216),AppliesWhen("Format","Text","Json","Csv")]
    public int MaximumCharacters{get;init;}=1024*1024;
    [DisplayName("JSON最大深度"),Range(1,128),AppliesWhen("Format","Json")]public int MaximumJsonDepth{get;init;}=64;
    [DisplayName("CSV分隔符"),AppliesWhen("Format","Csv")]public CsvSeparator CsvDelimiter{get;init;}
    [DisplayName("首行为表头"),Description("表头独立保存，数据行索引从0开始，不包含表头。"),AppliesWhen("Format","Csv")]
    public bool CsvHeader{get;init;}=true;
    [DisplayName("要求各行列数一致"),Description("严格解析引号和转义。关闭时允许不等长行，但选择不存在单元格仍报错。"),AppliesWhen("Format","Csv")]
    public bool RequireConsistentColumns{get;init;}=true;
    [DisplayName("最多CSV数据行"),Range(1,100000),AppliesWhen("Format","Csv")]public int MaximumRows{get;init;}=10000;
    [DisplayName("最多CSV列"),Range(1,1024),AppliesWhen("Format","Csv")]public int MaximumColumns{get;init;}=256;
    [DisplayName("最多CSV单元格"),Range(1,1000000),AppliesWhen("Format","Csv")]public int MaximumCells{get;init;}=100000;
}
public static class LocalFileReader
{
    public static IReadOnlyList<string> Validate(FileReadOptions p)
    {
        var errors=new List<string>();if(string.IsNullOrWhiteSpace(p.Path)||p.Path.Length>4096||p.Path.IndexOf('\0')>=0)errors.Add("请选择明确的本地文件路径（最长4096字符）。");
        if(!Enum.IsDefined(p.Format))errors.Add("文件读取格式无效。");if(p.MaximumBytes is <1 or >BinaryData.MaximumLength)errors.Add("文件字节预算必须为1..16777216。");
        if(p.Format!=LocalFileFormat.Binary&&(!Enum.IsDefined(p.Encoding)||!Enum.IsDefined(p.Bom)||p.MaximumCharacters is <1 or >16777216))errors.Add("文本编码、BOM或字符预算无效。");
        if(p.Format==LocalFileFormat.Json&&p.MaximumJsonDepth is <1 or >128)errors.Add("JSON深度必须为1..128。");
        if(p.Format==LocalFileFormat.Csv&&(!Enum.IsDefined(p.CsvDelimiter)||p.MaximumRows is <1 or >100000||p.MaximumColumns is <1 or >1024||p.MaximumCells is <1 or >1000000))errors.Add("CSV分隔符或行、列、单元格预算无效。");return errors;
    }
    public static string Resolve(string path,string resourceRoot)
    {
        if(string.IsNullOrWhiteSpace(path)||path.IndexOf('\0')>=0)throw new ArgumentException("本地文件路径不能为空。");
        if(path.StartsWith(@"\\",StringComparison.Ordinal)||path.StartsWith("//",StringComparison.Ordinal)||path.Contains("://",StringComparison.Ordinal))throw new ArgumentException("不允许网络、设备或URI文件路径。");
        if(System.IO.Path.IsPathRooted(path)&&!System.IO.Path.IsPathFullyQualified(path))throw new ArgumentException("文件路径必须为工程相对路径或完整本地绝对路径，不能使用盘符相对路径。");
        var root=System.IO.Path.GetFullPath(resourceRoot);var full=System.IO.Path.GetFullPath(System.IO.Path.IsPathFullyQualified(path)?path:System.IO.Path.Combine(root,path));
        if(!System.IO.Path.IsPathFullyQualified(path)&&!full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar)+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("工程相对文件路径不能离开工程根目录。");
        var drive=System.IO.Path.GetPathRoot(full)!;if(new DriveInfo(drive).DriveType==DriveType.Network)throw new ArgumentException("不允许从映射网络盘读取文件。");
        var parts=full.Split([System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar],StringSplitOptions.RemoveEmptyEntries);var sensitive=new HashSet<string>([".ssh",".aws",".azure",".kube",".gnupg","credentials","secrets"],StringComparer.OrdinalIgnoreCase);
        if(parts.Any(sensitive.Contains))throw new ArgumentException("不允许读取常见私钥、凭据或环境密钥目录。");var name=System.IO.Path.GetFileName(full);var extension=System.IO.Path.GetExtension(full);
        if(name.Equals(".env",StringComparison.OrdinalIgnoreCase)||name.StartsWith(".env.",StringComparison.OrdinalIgnoreCase)||name.StartsWith("id_rsa",StringComparison.OrdinalIgnoreCase)||name.StartsWith("id_ed25519",StringComparison.OrdinalIgnoreCase)||name.Equals("credentials.json",StringComparison.OrdinalIgnoreCase)||name.StartsWith("secrets.",StringComparison.OrdinalIgnoreCase)||new[]{".pem",".key",".pfx",".p12"}.Contains(extension,StringComparer.OrdinalIgnoreCase))throw new ArgumentException("不允许通过文件算子读取常见环境密钥、凭据或私钥文件。");
        for(var current=full;current is not null;current=System.IO.Path.GetDirectoryName(current))if(File.Exists(current)||Directory.Exists(current)){if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("文件及祖先目录不能包含重解析链接。");}
        return full;
    }
    public static async Task<FileReadResult> ReadAsync(FileReadOptions p,string resourceRoot,CancellationToken ct=default)
    {
        var errors=Validate(p);if(errors.Count>0)throw new ArgumentException(string.Join(";",errors));ct.ThrowIfCancellationRequested();var path=Resolve(p.Path,resourceRoot);
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(file.Length>p.MaximumBytes)throw new IOException($"文件超过{p.MaximumBytes}字节预算；未返回截断内容。");var data=new byte[checked((int)file.Length)];await file.ReadExactlyAsync(data,ct);if(await file.ReadAsync(new byte[1],ct)!=0)throw new IOException("文件读取期间长度发生变化；请稳定文件后重试。");ct.ThrowIfCancellationRequested();var hash=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if(p.Format==LocalFileFormat.Binary)return new(path,hash,p.Format,"binary",data.LongLength,bytes:new BinaryData(data));
        var (text,encoding)=Decode(data,p);if(text.Length>p.MaximumCharacters)throw new IOException("解码文本超过字符预算；未返回截断文本。");ct.ThrowIfCancellationRequested();
        var result=p.Format switch
        {
            LocalFileFormat.Text=>new(path,hash,p.Format,encoding,data.LongLength,text),
            LocalFileFormat.Json=>ReadJson(path,hash,encoding,data.LongLength,text,p),
            LocalFileFormat.Csv=>new(path,hash,p.Format,encoding,data.LongLength,text,table:ParseCsv(text,p,ct)),
            _=>throw new ArgumentException("不支持的文件格式。")
        };ct.ThrowIfCancellationRequested();return result;
    }
    private static FileReadResult ReadJson(string path,string hash,string encoding,long size,string text,FileReadOptions p)
    {
        try{using var doc=JsonDocument.Parse(text,new JsonDocumentOptions{AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow,MaxDepth=p.MaximumJsonDepth});return new(path,hash,LocalFileFormat.Json,encoding,size,text,doc.RootElement);}
        catch(JsonException ex){throw new JsonException("JSON格式或嵌套深度无效："+ex.Message,ex);}
    }
    private static (string Text,string Encoding) Decode(byte[] data,FileReadOptions p)
    {
        var encoding=p.Encoding;var skip=0;LocalTextEncoding? detected=null;
        if(data.Length>=4&&(data[0]==0xff&&data[1]==0xfe&&data[2]==0&&data[3]==0||data[0]==0&&data[1]==0&&data[2]==0xfe&&data[3]==0xff))throw new DecoderFallbackException("当前不支持UTF-32；请明确转换为UTF-8或UTF-16后读取。");
        if(data.Length>=3&&data[0]==0xef&&data[1]==0xbb&&data[2]==0xbf){detected=LocalTextEncoding.Utf8;skip=3;}
        else if(data.Length>=2&&data[0]==0xff&&data[1]==0xfe){detected=LocalTextEncoding.Utf16LE;skip=2;}
        else if(data.Length>=2&&data[0]==0xfe&&data[1]==0xff){detected=LocalTextEncoding.Utf16BE;skip=2;}
        if(detected is{}bom){if(p.Bom==BomHandling.RequireSelected&&bom!=p.Encoding)throw new DecoderFallbackException("文件BOM与所选编码不一致；请修改编码或选择识别BOM。");encoding=bom;}
        Encoding decoder=encoding switch{LocalTextEncoding.Utf8=>new UTF8Encoding(false,true),LocalTextEncoding.Utf16LE=>new UnicodeEncoding(false,false,true),LocalTextEncoding.Utf16BE=>new UnicodeEncoding(true,false,true),_=>throw new ArgumentException("不支持的文本编码。")};return(decoder.GetString(data,skip,data.Length-skip),decoder.WebName);
    }
    public static TabularData ParseCsv(string text,FileReadOptions p,CancellationToken ct=default)
    {
        if(text.Length>p.MaximumCharacters||!Enum.IsDefined(p.CsvDelimiter)||p.MaximumRows is <1 or >100000||p.MaximumColumns is <1 or >1024||p.MaximumCells is <1 or >1000000)throw new ArgumentException("CSV文本、行、列、单元格或分隔符预算无效。");
        var delimiter=p.CsvDelimiter switch{CsvSeparator.Semicolon=>';',CsvSeparator.Tab=>'\t',_=>','};var rows=new List<IReadOnlyList<string>>();var row=new List<string>();var cell=new StringBuilder();var state=0;var started=false;var totalCells=0;int? columns=null;
        void EndCell(){if(row.Count>=p.MaximumColumns||++totalCells>p.MaximumCells)throw new FormatException("CSV列数或单元格总数超过预算。");row.Add(cell.ToString());cell.Clear();state=0;started=false;}
        void EndRow(){EndCell();if(p.RequireConsistentColumns&&columns is{}n&&row.Count!=n)throw new FormatException($"CSV第{rows.Count+1}行列数与首行不同。");columns??=row.Count;if(rows.Count>=p.MaximumRows+(p.CsvHeader?1:0))throw new FormatException("CSV数据行数超过预算。");rows.Add(Array.AsReadOnly(row.ToArray()));row.Clear();}
        for(var i=0;i<text.Length;i++)
        {
            if((i&4095)==0)ct.ThrowIfCancellationRequested();var c=text[i];
            if(state==2){if(c=='"')state=3;else cell.Append(c);continue;}
            if(state==3&&c=='"'){cell.Append('"');state=2;continue;}
            if(c==delimiter){EndCell();continue;}
            if(c is '\r' or '\n'){if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;EndRow();continue;}
            if(state==3)throw new FormatException($"CSV位置{i}：闭合引号后只允许分隔符或换行，未忽略额外字符。");
            if(c=='"'){if(state!=0||cell.Length!=0)throw new FormatException($"CSV位置{i}：引号必须位于字段开头。");state=2;started=true;continue;}
            cell.Append(c);state=1;started=true;
        }
        ct.ThrowIfCancellationRequested();if(state==2)throw new FormatException("CSV存在未闭合的引号字段。");if(started||state==3||cell.Length>0||row.Count>0)EndRow();var headers=p.CsvHeader&&rows.Count>0?rows[0]:[];return new(headers,p.CsvHeader?rows.Skip(1):rows);
    }
}

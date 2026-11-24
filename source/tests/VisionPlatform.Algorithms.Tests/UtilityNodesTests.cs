using System.Text;
using System.Text.Json;
using VisionPlatform.Algorithms.Utilities;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using Xunit;

namespace VisionPlatform.Algorithms.Tests;
public sealed class UtilityNodesTests
{
    private static string Root(){var root=Path.Combine(Path.GetTempPath(),"VisionStudio-utility-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static NodeSpecification Node(string id,string type,object p)=>new(id,type,type,JsonDefaults.Element(p));
    private static async Task<(double[] Values,long Sequence)> Random(RuntimeExecutionSession session,RandomNumberOptions p,int? index=null)
    {
        using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry(),null,session).ExecuteSingleAsync(Node("rng","RandomNumber",p),index is null?new Dictionary<string,object>():new Dictionary<string,object>{{"IterationIndex",index.Value}});Assert.True(run.Success,run.ErrorMessage);return(run.Nodes["rng"].Output<VisionCollection>("Collection").Items.Select(i=>i.Fields["Number"].Number!.Value).ToArray(),run.Nodes["rng"].Output<long>("Sequence"));
    }
    [Fact]public async Task FixedSeedReproducesAcrossSessionsAndAdvancesWithinOneSession()
    {
        var p=new RandomNumberOptions{Kind=RandomNumberKind.Real,Mode=RandomOutputMode.Collection,Minimum=-2,Maximum=3,Seed=52,Count=20};await using var a=new RuntimeExecutionSession();await using var b=new RuntimeExecutionSession();var first=await Random(a,p);var second=await Random(a,p);var repeated=await Random(b,p);Assert.Equal(first.Values,repeated.Values);Assert.NotEqual(first.Values,second.Values);Assert.Equal(1,first.Sequence);Assert.Equal(2,second.Sequence);Assert.All(first.Values,v=>Assert.True(v>=-2&&v<3));
        var changed=await Random(a,p with{Seed=53});Assert.Equal(1,changed.Sequence);Assert.NotEqual(first.Values,changed.Values);
    }
    [Fact]public async Task IntegerBoundsAndSingleVersusCollectionAreExplicit()
    {
        await using var session=new RuntimeExecutionSession();var p=new RandomNumberOptions{MinimumInteger=-1,MaximumInteger=0,Mode=RandomOutputMode.Collection,Count=100,Seed=1};var r=await Random(session,p);Assert.All(r.Values,v=>Assert.Equal(-1,v));
        var runtime=new RuntimeExecutor(BuiltinRegistration.CreateRegistry(),null,session);using(var multiple=await runtime.ExecuteSingleAsync(Node("rng","RandomNumber",p),new Dictionary<string,object>())){Assert.False(multiple.Nodes["rng"].Outputs.ContainsKey("Value"));Assert.Equal(100,multiple.Nodes["rng"].Output<int>("Count"));}
        using(var single=await runtime.ExecuteSingleAsync(Node("rng","RandomNumber",p with{Mode=RandomOutputMode.Single,Count=0}),new Dictionary<string,object>())){Assert.True(single.Success,single.ErrorMessage);Assert.Equal(-1,single.Nodes["rng"].Output<double>("Value"));Assert.Equal(1,single.Nodes["rng"].Output<int>("Count"));}
        var extreme=await Random(session,p with{MinimumInteger=int.MinValue,MaximumInteger=int.MaxValue,Count=1000});Assert.All(extreme.Values,v=>Assert.True(v>=int.MinValue&&v<int.MaxValue&&Math.Truncate(v)==v));
    }
    [Fact]public async Task PerIndexStreamsAreDeterministicAndConcurrentAccessIsSerialized()
    {
        var p=new RandomNumberOptions{Kind=RandomNumberKind.Real,Seed=12};await using var a=new RuntimeExecutionSession();await using var b=new RuntimeExecutionSession();var tasks=Enumerable.Range(0,8).Select(i=>Task.Run(()=>Random(a,p,i))).ToArray();var parallel=await Task.WhenAll(tasks);for(var i=0;i<parallel.Length;i++)Assert.Equal(parallel[i].Values,(await Random(b,p,i)).Values);Assert.True(parallel.Select(r=>r.Values[0]).Distinct().Count()>1);Assert.Equal(2,(await Random(a,p,3)).Sequence);
    }
    [Fact]public async Task RandomParticipatesInTheExistingConstrainedSubflowWithExplicitIndex()
    {
        var registry=BuiltinRegistration.CreateRegistry();var definition=new SubflowDefinition{Nodes=[Node("current","IterationInput",new IterationInputOptions()),Node("rng","RandomNumber",new RandomNumberOptions{Kind=RandomNumberKind.Real,Seed=23}),Node("collect","IterationOutput",new IterationOutputOptions())],Connections=[new("current","Index","rng","IterationIndex"),new("rng","Value","collect","Number")]};
        Assert.Empty(SubflowValidation.Validate(registry,definition));Assert.Contains(SubflowValidation.Validate(registry,definition with{Connections=definition.Connections.Skip(1).ToList()}),e=>e.Contains("IterationIndex",StringComparison.Ordinal));
        var collection=new VisionCollection(VisionItemKind.Scalar,Enumerable.Range(0,4).Select(i=>new ScalarItem(new(Guid.NewGuid(),"input",i,null,"scalar",i.ToString()),(double)i)));double[]? canonical=null;
        for(var pass=0;pass<2;pass++){using var run=await new RuntimeExecutor(registry).ExecuteSingleAsync(Node("each","ForEach",new ForEachOptions{Body=definition}),new Dictionary<string,object>{{"Collection",collection}});Assert.True(run.Success,run.ErrorMessage);var result=run.Nodes["each"].Output<VisionCollection>("Results");var values=result.Items.Cast<IterationItem>().Select(i=>i.Result!.Number!.Value).ToArray();if(canonical is null)canonical=values;else Assert.Equal(canonical,values);}
    }
    [Fact]public async Task RandomInvalidRangesAndCancelledRunsDoNotReturnPlaceholderValues()
    {
        var registry=BuiltinRegistration.CreateRegistry();foreach(var p in new[]{new RandomNumberOptions{MinimumInteger=2,MaximumInteger=2},new RandomNumberOptions{Kind=RandomNumberKind.Real,Minimum=-double.MaxValue,Maximum=double.MaxValue},new RandomNumberOptions{Mode=RandomOutputMode.Collection,Count=10001}}){using var run=await new RuntimeExecutor(registry).ExecuteSingleAsync(Node("rng","RandomNumber",p),new Dictionary<string,object>());Assert.False(run.Success);Assert.Empty(run.Nodes["rng"].Outputs);}
        using var cancel=new CancellationTokenSource();cancel.Cancel();using var cancelled=await new RuntimeExecutor(registry).ExecuteSingleAsync(Node("rng","RandomNumber",new RandomNumberOptions()),new Dictionary<string,object>(),cancellationToken:cancel.Token);Assert.Equal(NodeExecutionStatus.Cancelled,cancelled.Status);Assert.Empty(cancelled.Nodes["rng"].Outputs);
    }
    [Theory][InlineData(LocalTextEncoding.Utf8)][InlineData(LocalTextEncoding.Utf16LE)][InlineData(LocalTextEncoding.Utf16BE)]
    public async Task TextEncodingBomAndLineBreaksAreRealAndHandlesAreReleased(LocalTextEncoding encoding)
    {
        var root=Root();var path=Path.Combine(root,"text.txt");Encoding encoder=encoding switch{LocalTextEncoding.Utf8=>new UTF8Encoding(true),LocalTextEncoding.Utf16LE=>new UnicodeEncoding(false,true),_=>new UnicodeEncoding(true,true)};await File.WriteAllBytesAsync(path,encoder.GetPreamble().Concat(encoder.GetBytes("Hello世界\r\nLine2\n")).ToArray());
        var result=await LocalFileReader.ReadAsync(new(){Path="text.txt",Encoding=LocalTextEncoding.Utf8},root);Assert.Equal("Hello世界\r\nLine2\n",result.Text);Assert.Equal(encoder.WebName,result.Encoding);Assert.Equal(new FileInfo(path).Length,result.ByteCount);using var exclusive=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }
    [Fact]public async Task InvalidEncodingBomMismatchEmptyTextAndBudgetFailuresAreClear()
    {
        var root=Root();var path=Path.Combine(root,"bad.txt");await File.WriteAllBytesAsync(path,[0xc3,0x28]);await Assert.ThrowsAsync<DecoderFallbackException>(()=>LocalFileReader.ReadAsync(new(){Path=path},root));await File.WriteAllBytesAsync(path,[0xff,0xfe,0x41,0]);await Assert.ThrowsAsync<DecoderFallbackException>(()=>LocalFileReader.ReadAsync(new(){Path=path,Bom=BomHandling.RequireSelected,Encoding=LocalTextEncoding.Utf8},root));await Assert.ThrowsAsync<IOException>(()=>LocalFileReader.ReadAsync(new(){Path=path,MaximumBytes=2},root));
        await File.WriteAllTextAsync(path,"",new UTF8Encoding(false));Assert.Equal("",(await LocalFileReader.ReadAsync(new(){Path=path},root)).Text);await File.WriteAllTextAsync(path,"hello");await Assert.ThrowsAsync<IOException>(()=>LocalFileReader.ReadAsync(new(){Path=path,MaximumCharacters=2},root));
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))await Assert.ThrowsAsync<IOException>(()=>LocalFileReader.ReadAsync(new(){Path=path},root));
    }
    [Fact]public async Task LocalPathBoundariesAndSensitiveLocationsAreNotImplicitlyRead()
    {
        var root=Root();foreach(var path in new[]{"../outside.txt",@"\\localhost\share\file.txt",@"\\?\C:\file.txt","https://example.test/file.txt",".env","sub/.env.production",".ssh/id_rsa","credentials.json","file.pem",@"C:relative.txt"})Assert.ThrowsAny<ArgumentException>(()=>LocalFileReader.Resolve(path,root));
        await Assert.ThrowsAsync<FileNotFoundException>(()=>LocalFileReader.ReadAsync(new(){Path="missing.txt"},root));using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAsync<OperationCanceledException>(()=>LocalFileReader.ReadAsync(new(){Path="missing.txt"},root,cancel.Token));
    }
    [Fact]public async Task CsvHandlesEscapedQuotesQuotedNewlinesHeadersAndZeroBasedCells()
    {
        var root=Root();var path=Path.Combine(root,"data.csv");await File.WriteAllTextAsync(path,"name,value,note\r\n\"a,b\",12.5,\"line1\r\nline2\"\r\n\"say \"\"hi\"\"\",false,end\r\n",new UTF8Encoding(false));var source=await LocalFileReader.ReadAsync(new(){Path=path,Format=LocalFileFormat.Csv},root);Assert.Equal(2,source.Table!.RowCount);Assert.Equal(3,source.Table.ColumnCount);Assert.Equal("a,b",source.Table.Cell(0,0));Assert.Equal("line1\r\nline2",source.Table.Cell(0,2));Assert.Equal("say \"hi\"",source.Table.Cell(1,0));Assert.Equal("name",source.Table.Headers[0]);
        using var number=await Value(source,new(){Selection=FileValueSelection.CsvCell,Row=0,Column=1,Target=FileValueTarget.Number});Assert.Equal(12.5,number.Nodes["value"].Output<double>("Number"));using var boolean=await Value(source,new(){Selection=FileValueSelection.CsvCell,Row=1,Column=1,Target=FileValueTarget.Boolean});Assert.False(boolean.Nodes["value"].Output<bool>("Boolean"));
    }
    [Theory][InlineData("a,b\n\"unterminated,b")][InlineData("a,b\n1,\"x\"extra")][InlineData("a,b\n1,a\"b")][InlineData("a,b\n1,2,3")]
    public async Task CsvMalformedInputsAreRejectedInsteadOfSplitOrSilentlyTruncated(string text)
    {var root=Root();var file=Path.Combine(root,"bad.csv");await File.WriteAllTextAsync(file,text);await Assert.ThrowsAsync<FormatException>(()=>LocalFileReader.ReadAsync(new(){Path=file,Format=LocalFileFormat.Csv},root));}
    [Fact]public void CsvEmptyTrailingFieldDelimiterAndReturnBudgetsAreExplicit()
    {
        var p=new FileReadOptions{Format=LocalFileFormat.Csv,CsvHeader=false};Assert.Equal(0,LocalFileReader.ParseCsv("",p).RowCount);var row=LocalFileReader.ParseCsv("\"\",value,",p);Assert.Equal("",row.Cell(0,0));Assert.Equal("",row.Cell(0,2));Assert.Equal("b",LocalFileReader.ParseCsv("a;b",p with{CsvDelimiter=CsvSeparator.Semicolon}).Cell(0,1));Assert.Equal("b",LocalFileReader.ParseCsv("a\tb",p with{CsvDelimiter=CsvSeparator.Tab}).Cell(0,1));
        Assert.Throws<FormatException>(()=>LocalFileReader.ParseCsv("a,b",p with{MaximumColumns=1}));Assert.Throws<FormatException>(()=>LocalFileReader.ParseCsv("a\nb",p with{MaximumRows=1}));Assert.Throws<FormatException>(()=>LocalFileReader.ParseCsv("a,b",p with{MaximumCells=1}));
    }
    private static async Task<RunResult> Value(FileReadResult source,FileValueOptions p)=>await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(Node("value","FileValue",p),new Dictionary<string,object>{{"Result",source}});
    [Fact]public async Task JsonPointerUsesRealEscapingAndPreservesRawPrecision()
    {
        var root=Root();var file=Path.Combine(root,"data.json");await File.WriteAllTextAsync(file,"{\"a/b\":{\"~key\":[{\"n\":0.12345678901234567890123,\"yes\":true}]},\"large\":9007199254740993,\"numericString\":\"12\"}");var source=await LocalFileReader.ReadAsync(new(){Path=file,Format=LocalFileFormat.Json},root);
        using(var text=await Value(source,new(){Selection=FileValueSelection.JsonPointer,Pointer="/a~1b/~0key/0/n"})){Assert.True(text.Success,text.ErrorMessage);Assert.Equal("0.12345678901234567890123",text.Nodes["value"].Output<string>("Text"));}
        using(var boolean=await Value(source,new(){Selection=FileValueSelection.JsonPointer,Pointer="/a~1b/~0key/0/yes",Target=FileValueTarget.Boolean})){Assert.True(boolean.Success,boolean.ErrorMessage);Assert.True(boolean.Nodes["value"].Output<bool>("Boolean"));}
        foreach(var p in new[]{new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/large",Target=FileValueTarget.Number},new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/numericString",Target=FileValueTarget.Number},new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/a~1b/~0key/00/n"},new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/bad~2escape"},new FileValueOptions{Selection=FileValueSelection.JsonPointer,Pointer="/missing"}}){using var run=await Value(source,p);Assert.False(run.Success);Assert.Empty(run.Nodes["value"].Outputs);}
    }
    [Fact]public async Task InvalidJsonDepthAndChangedFilesCannotReuseAnOldSnapshot()
    {
        var root=Root();var file=Path.Combine(root,"data.json");await File.WriteAllTextAsync(file,"{\"x\":1}");var p=new FileReadOptions{Path=file,Format=LocalFileFormat.Json};var first=await LocalFileReader.ReadAsync(p,root);await File.WriteAllTextAsync(file,"{\"x\":2}");var second=await LocalFileReader.ReadAsync(p,root);Assert.NotEqual(first.Sha256,second.Sha256);Assert.Equal(1,first.Json!.Value.GetProperty("x").GetInt32());Assert.Equal(2,second.Json!.Value.GetProperty("x").GetInt32());
        await File.WriteAllTextAsync(file,"{\"x\":}");await Assert.ThrowsAsync<JsonException>(()=>LocalFileReader.ReadAsync(p,root));await File.WriteAllTextAsync(file,"[[[[0]]]]");await Assert.ThrowsAsync<JsonException>(()=>LocalFileReader.ReadAsync(p with{MaximumJsonDepth=2},root));
    }
    [Fact]public async Task BinaryReadIsImmutableBoundedAndSelectedWithoutDefaultValues()
    {
        var root=Root();var file=Path.Combine(root,"data.bin");await File.WriteAllBytesAsync(file,[0,128,255,10]);var source=await LocalFileReader.ReadAsync(new(){Path=file,Format=LocalFileFormat.Binary,MaximumRows=0},root);Assert.Null(source.Text);var cloned=source.Bytes!.ToArray();cloned[1]=0;Assert.Equal((byte)128,source.Bytes[1]);
        using(var number=await Value(source,new(){Selection=FileValueSelection.BinaryByte,Offset=1,Target=FileValueTarget.Number})){Assert.True(number.Success,number.ErrorMessage);Assert.Equal(128,number.Nodes["value"].Output<double>("Number"));}
        using(var slice=await Value(source,new(){Selection=FileValueSelection.BinarySlice,Offset=1,Length=2,Target=FileValueTarget.Bytes})){Assert.True(slice.Success,slice.ErrorMessage);Assert.Equal(new byte[]{128,255},slice.Nodes["value"].Output<BinaryData>("Bytes").ToArray());}
        using(var invalid=await Value(source,new(){Selection=FileValueSelection.BinaryByte,Offset=4,Target=FileValueTarget.Number})){Assert.False(invalid.Success);Assert.Empty(invalid.Nodes["value"].Outputs);}
    }
    [Theory][InlineData("not-boolean",FileValueTarget.Boolean)][InlineData("1e-999",FileValueTarget.Number)][InlineData("NaN",FileValueTarget.Number)][InlineData("9007199254740993",FileValueTarget.Number)]
    public async Task ExplicitFieldConversionsFailWithoutSilentFalseZeroOrPrecisionLoss(string text,FileValueTarget target)
    {var source=new FileReadResult("snapshot","hash",LocalFileFormat.Text,"utf-8",text.Length,text);using var run=await Value(source,new(){Target=target});Assert.False(run.Success);Assert.Empty(run.Nodes["value"].Outputs);}
    [Fact]public async Task FileReadAndValueUseFormalNodeParametersAndOriginalRuntime()
    {
        var root=Root();await File.WriteAllTextAsync(Path.Combine(root,"numbers.csv"),"name,value\ncount,7\n");var registry=BuiltinRegistration.CreateRegistry();using var run=await new RuntimeExecutor(registry).ExecuteAsync(new([Node("read","FileRead",new FileReadOptions{Path="numbers.csv",Format=LocalFileFormat.Csv}),Node("value","FileValue",new FileValueOptions{Selection=FileValueSelection.CsvCell,Row=0,Column=1,Target=FileValueTarget.Number})],[new("read","Result","value","Result")]),root);Assert.True(run.Success,run.ErrorMessage);Assert.Equal(7,run.Nodes["value"].Output<double>("Number"));Assert.IsType<FileReadResult>(run.Nodes["read"].Outputs["Result"]);
    }
}

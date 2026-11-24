using System.Globalization;
using System.Windows.Threading;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Wpf.Parameters;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;
public sealed class MatrixTableEditorTests
{
    [Fact] public void AbandoningTableDraftPreservesOriginalTextAndExplicitApplyPreservesPrecision()=>Sta(()=>
    {
        const string text="1.2345678901234567, 0; 0, 1";var catalog=new NodeAuthoringCatalog();var draft=new ParameterDraft(catalog.Get("MatrixCreate"),JsonDefaults.Element(new MatrixCreateOptions{ValuesText=text}));var field=draft.Fields.Single(f=>f.Descriptor.Path=="ValuesText");var editor=new MatrixTableEditor(field,draft,"MatrixCreate");var abandoned=editor.CreateTableDraft();abandoned.Cells[0][0]="5";Assert.Equal(text,field.Text);
        var accepted=editor.CreateTableDraft();Assert.True(editor.ApplyTableDraft(accepted));var value=MatrixGeometryProcessor.Parse(2,2,field.Text)[0,0];Assert.Equal(double.Parse("1.2345678901234567",CultureInfo.InvariantCulture),value);
    });
    [Fact] public void InvalidCellsStayVisibleAndNeverBecomeZero()=>Sta(()=>
    {
        var draft=new ParameterDraft(new NodeAuthoringCatalog().Get("MatrixCreate"),JsonDefaults.Element(new MatrixCreateOptions()));var field=draft.Fields.Single(f=>f.Descriptor.Path=="ValuesText");var original=field.Text;var editor=new MatrixTableEditor(field,draft,"MatrixCreate");var table=editor.CreateTableDraft();table.Cells[0][1]="not a number";Assert.False(editor.ApplyTableDraft(table));Assert.Equal(original,field.Text);Assert.Equal("not a number",table.Cells[0][1]);Assert.False(table.TryBuild(out _,out var error,out var row,out var column));Assert.Equal(0,row);Assert.Equal(1,column);Assert.Contains("第1行、第2列",error);
    });
    [Fact] public void MatrixTableHasExplicitBudgetAndNeverTruncatesExistingNumbers()
    {Assert.Throws<ArgumentException>(()=>new MatrixTableDraft(33,1,""));Assert.Throws<ArgumentException>(()=>new MatrixTableDraft(1,1,"1,2"));Assert.Throws<ArgumentException>(()=>new MatrixTableDraft(1,1,"1;2"));var missing=new MatrixTableDraft(1,2,"1");Assert.Equal("",missing.Cells[0][1]);Assert.False(missing.TryBuild(out _,out _,out _,out _));}
    private static void Sta(Action action)
    {Exception? error=null;var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());action();}catch(Exception ex){error=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(20)));if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();}
}

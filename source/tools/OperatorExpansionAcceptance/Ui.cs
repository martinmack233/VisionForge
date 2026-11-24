using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Algorithms.Transforms;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Collections;
using VisionPlatform.Editor.Wpf.Imaging;
using VisionPlatform.Editor.Wpf.Parameters;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;

internal static partial class Program
{
    private static int Ui(string root,string? providedExamples)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new(){Source=new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml")});var code=1;
        app.Startup+=async(_,_)=>
        {
            MainWindow? window=null;EditorSession? session=null;var bytesBefore=FrameHandle.ActiveBytes;
            try
            {
                var config=Path.Combine(root,"machine.json");Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG",config);await new MachineConfigurationStore(config).SaveAsync(new(){SetupCompleted=true,DataDirectory=Path.Combine(root,"data"),CacheDirectory=Path.Combine(root,"cache"),LogDirectory=Path.Combine(root,"logs")});
                var examples=providedExamples is null?Path.Combine(root,"source"):Path.GetFullPath(providedExamples);if(providedExamples is null)await OperatorExpansionSamples.SaveAllAsync(examples);var catalog=new ExampleCatalog(examples,new ProjectStore(BuiltinRegistration.CreateRegistry()));var entries=await catalog.LoadAsync();
                session=new EditorSession();window=new(session,Path.Combine(root,"state")){Width=1440,Height=940,Left=30,Top=30};window.Show();await window.WorkspaceReady;var rows=new List<object>();var tabs=(TabControl)window.FindName("InspectorTabs");var viewport=(ImageViewportControl)window.FindName("Viewport");
                foreach(var id in OperatorExpansionSamples.Ids)
                {
                    var entry=entries.Single(e=>e.Manifest.Id==id);Check((await catalog.ValidateAsync(entry)).Count==0,"Example validation failed: "+id);var path=await catalog.CopyAsync(entry,Path.Combine(root,"writable"));await session.OpenAsync(path);window.Navigate(WorkspacePage.Editor);await session.RunAsync();Check(session.Results.All(n=>n.Status=="Success"),"WPF node execution failed: "+id+" "+session.StatusMessage);
                    window.FitEditorViews();window.UpdateLayout();
                    if(id==OperatorExpansionSamples.Ids[0])
                    {
                        session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="display");tabs.SelectedIndex=2;window.UpdateLayout();var table=((CollectionResultsView)window.FindName("CollectionsView")).ResultTable;Check(table.Items.Count==2,"Expected two contour rows");table.SelectedIndex=0;Check(session.CollectionSelection is not null,"Collection highlight missing");await viewport.WaitForDisplayAsync();await Capture(window,root,"01-enhanced-contours.png");
                        Check(session.InspectOutput("input","Image"),"Original image output missing");await viewport.WaitForDisplayAsync();await Capture(window,root,"02-original-low-contrast.png");Check(session.InspectOutput("gradient_x","Derivative"),"Float32 output missing");tabs.SelectedIndex=1;Check(session.SelectedOutputInspection?.DataType==nameof(FloatPlaneFrame),"Raw derivative mislabeled as Mono8");await Capture(window,root,"03-float-derivative-details.png");Check(session.InspectOutput("gradient_x","Preview"),"8-bit preview missing");await viewport.WaitForDisplayAsync();await Capture(window,root,"04-explicit-gradient-preview.png");
                    }
                    else if(id==OperatorExpansionSamples.Ids[1])
                    {
                        session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="display");tabs.SelectedIndex=2;window.UpdateLayout();var table=((CollectionResultsView)window.FindName("CollectionsView")).ResultTable;Check(table.Items.Count==1,"Mapped ROI collection missing");table.SelectedIndex=0;await viewport.WaitForDisplayAsync();await Capture(window,root,"05-perspective-quadrilateral.png");
                        session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="precision_matrix");tabs.SelectedIndex=0;window.UpdateLayout();var panel=(ParameterPanel)window.FindName("ParametersView");var editor=Find<MatrixTableEditor>(panel).Single();var field=panel.Draft!.Fields.Single(f=>f.Descriptor.Path=="ValuesText");var original=field.Text;
                        var opening=Dispatcher.CurrentDispatcher.BeginInvoke(()=>editor.OpenTableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(200);var dialog=app.Windows.Cast<Window>().Single(w=>w.Title.StartsWith("矩阵表格",StringComparison.Ordinal));await Capture(dialog,root,"06-matrix-table-dialog.png");dialog.Close();await opening.Task;Check(field.Text==original,"Closing table rewrote the original value");
                        opening=Dispatcher.CurrentDispatcher.BeginInvoke(()=>editor.OpenTableButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));await Task.Delay(150);dialog=app.Windows.Cast<Window>().Single(w=>w.Title.StartsWith("矩阵表格",StringComparison.Ordinal));Find<Button>(dialog).Single(b=>b.Content?.ToString()=="确认写入草稿").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await opening.Task;Check(MatrixGeometryProcessor.Parse(2,2,field.Text)[0,0]==1.2345678901234567,"Matrix confirmation lost Float64 precision");Check(panel.ApplyChanges(),"Matrix draft apply failed");Check(session.CurrentOutputs.Count==0,"Applying coefficients left stale results");await session.RunAsync();Check(session.Results.All(n=>n.Status=="Success"),"Matrix rerun after apply failed");await Capture(window,root,"07-matrix-parameters.png");
                    }
                    else if(id==OperatorExpansionSamples.Ids[2])
                    {
                        Check((bool)session.CurrentOutputs["boolean"]["Result"],"Typed AND result wrong");session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="boolean");tabs.SelectedIndex=0;await Capture(window,root,"08-unified-boolean-parameters.png");session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="display");tabs.SelectedIndex=2;window.UpdateLayout();Check(((CollectionResultsView)window.FindName("CollectionsView")).ResultTable.Items.Count==4,"Random collection rows missing");await Capture(window,root,"09-random-collection.png");
                    }
                    else
                    {
                        var csv=(FileReadResult)session.CurrentOutputs["csv"]["Result"];Check(csv.Table?.Cell(0,0)=="A,one"&&csv.Table.Cell(0,2)=="quoted \"text\"\r\nsecond line","CSV parsing flattened quotes/newlines");session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="csv_note");tabs.SelectedIndex=0;await Capture(window,root,"10-csv-field-parameters.png");Check(session.InspectOutput("csv","Result"),"CSV result not inspectable");tabs.SelectedIndex=1;await Capture(window,root,"11-file-table-details.png");Check(session.InspectOutput("csv_note","Text"),"CSV selected value missing");Check(session.SelectedOutputInspection?.FullText=="quoted \"text\"\r\nsecond line","Full selected text changed");
                    }
                    rows.Add(new{Id=id,ActualExecution=true,SavedWritableCopy=path});
                }
                window.Width=1024;window.Height=720;await Capture(window,root,"12-narrow-file-details.png");var saved=Path.Combine(session.ResourceRoot,"saved-reopened.json");await session.SaveAsync(saved);await session.NewAsync();Check(session.DisplayImage is null&&session.CurrentOutputs.Count==0,"Project switch retained stale output");await session.OpenAsync(saved);await session.RunAsync();Check(session.Results.All(n=>n.Status=="Success"),"Save/reopen failed");
                var commRoot=Path.Combine(root,"communication-source");await OperatorCommunicationDemo.SaveAllAsync(commRoot);await window.Examples.ReloadAsync(commRoot);
                foreach(var id in OperatorCommunicationDemo.Ids){window.Navigate(WorkspacePage.Examples);window.Examples.Selected=window.Examples.Items.Single(e=>e.Manifest.Id==id);await window.Examples.RunSelectedAsync(true);Check(window.Examples.LastVerification?.Passed==true,"WPF communication verification failed: "+window.Examples.Status);window.Navigate(WorkspacePage.Editor);session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="connect");tabs.SelectedIndex=0;window.Width=1440;window.Height=940;await Capture(window,root,id+"-parameters.png");rows.Add(new{Id=id,ActualExecution=true,LoopbackOnly=true});await session.SaveAsync(session.ProjectPath!);}
                await window.Examples.RunRtuSimulationAsync();Check(window.Examples.LastVerification?.Passed==true,"WPF RTU simulation failed");window.Navigate(WorkspacePage.Examples);await Capture(window,root,"rtu-simulation-result.png");
                var dpi=VisualTreeHelper.GetDpi(window);await Report(root,"ui-summary.json",new{Passed=true,ActualShownWindows=true,ProgrammaticInteractions=true,NativeInput=false,Examples=rows,Float32AndPreviewSeparated=true,MatrixDialogCancelPreservedOriginal=true,MatrixConfirmedPrecision=true,ApplyInvalidatesOldResults=true,FileCsvQuotedNewline=true,SaveReopen=true,Narrow="1024x720",ActualDpiScaleX=dpi.DpiScaleX,ActualDpiScaleY=dpi.DpiScaleY,Hardware=false,Training=false});code=0;
            }
            catch(Exception ex){Console.Error.WriteLine(ex);await File.WriteAllTextAsync(Path.Combine(root,"failure.txt"),ex.ToString());}
            finally
            {
                if(window is not null){await window.AiPage.PrepareCloseAsync(_=>Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks));await window.CloseEditorAsync(true);}session?.Dispose();await Report(root,"ui-release.json",new{FrameBytesBefore=bytesBefore,FrameBytesAfter=FrameHandle.ActiveBytes,AllOwnedFramesReleased=FrameHandle.ActiveBytes==bytesBefore});if(FrameHandle.ActiveBytes!=bytesBefore)code=1;app.Shutdown(code);
            }
        };
        app.Run();return code;
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T:DependencyObject
    {if(parent is T own)yield return own;for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(parent,i)))yield return child;}
    private static async Task Capture(Window window,string root,string file)
    {window.UpdateLayout();await Task.Delay(160);await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(root,file));png.Save(output);}
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Collections;
using VisionPlatform.Plugins.Samples;

internal static partial class Program
{
    private static int Ui(string root,string exampleRoot)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var code=1;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml")});
        app.Startup+=async(_,_)=>
        {
            MainWindow? window=null;EditorSession? session=null;
            try
            {
                var machine=Path.Combine(root,"machine.json");Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG",machine);await new MachineConfigurationStore(machine).SaveAsync(new(){DataDirectory=Path.Combine(root,"data"),CacheDirectory=Path.Combine(root,"cache"),LogDirectory=Path.Combine(root,"logs"),SetupCompleted=true});
                session=new EditorSession();window=new(session,Path.Combine(root,"state")){Width=1440,Height=940,Left=-20000,Top=-20000,ShowInTaskbar=false};window.Show();await window.WorkspaceReady;
                var outcomes=new List<object>();
                for(var i=0;i<3;i++)
                {
                    var file=Path.GetFullPath(Path.Combine(exampleRoot,InspectionExampleFactory.Ids[i],"project.json"));await window.Workspace.OpenProjectAsync(file);window.Navigate(WorkspacePage.Editor);await session.RunAsync();Check(session.Results.All(n=>n.Status=="Success"),"WPF graph failed");session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="display");
                    ((VisionPlatform.Editor.Wpf.Graph.GraphEditorView)window.FindName("GraphView")).FitToContent();((TabControl)window.FindName("InspectorTabs")).SelectedIndex=2;window.UpdateLayout();var table=((CollectionResultsView)window.FindName("CollectionsView")).ResultTable;Check(table.Items.Count>0,"Actual result rows missing");table.SelectedIndex=0;Check(session.CollectionSelection is not null,"Selection highlight association missing");
                    await Capture(window,root,$"0{i+1}-{InspectionExampleFactory.Ids[i]}.png");outcomes.Add(new{Id=InspectionExampleFactory.Ids[i],Rows=table.Items.Count,Selection=true});
                    if(i==1){((TabControl)window.FindName("InspectorTabs")).SelectedIndex=0;session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="range");await Capture(window,root,"04-color-parameters.png");}
                }
                session.SelectedNode=session.Nodes.Single(n=>n.NodeId=="each");var dialog=new SubflowEditorWindow(session.Registry,session.ResourceRoot){Owner=window,Left=-18000,Top=-18000,ShowInTaskbar=false};await dialog.LoadBodyAsync(InspectionExampleFactory.GeometryMeasure());dialog.Show();await Capture(dialog,root,"05-geometry-subflow.png");dialog.Close();
                window.Width=1000;window.Height=650;((TabControl)window.FindName("InspectorTabs")).SelectedIndex=2;await Capture(window,root,"06-narrow.png");
                var saved=Path.Combine(root,"reopened.json"); // Save within the imported project so relative resources remain valid.
                saved=Path.Combine(Path.GetDirectoryName(session.ProjectPath!)!,"wpf-saved.json");await session.SaveAsync(saved);await session.NewAsync();Check(session.DisplayImage is null&&session.CollectionDisplay is null,"Stale display on project switch");await session.OpenAsync(saved);await session.RunAsync();Check(session.Results.All(n=>n.Status=="Success"),"WPF save/reopen failed");
                await Report(root,"3c-wpf-summary.json",new{Passed=true,ActualShownWindows=true,ProgrammaticInteractions=true,NativeInput=false,Outcomes=outcomes,SubflowEditor=true,SaveReopen=true,Narrow="1000x650",TrainingStarted=false,RealCamera=false});code=0;
            }
            catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(root,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);}
            finally{if(window is not null){await window.AiPage.PrepareCloseAsync(_=>Task.FromResult(VisionPlatform.Editor.Wpf.AI.AiCloseChoice.StopTasks));await window.CloseEditorAsync(true);}session?.Dispose();app.Shutdown(code);}
        };
        app.Run();return code;
    }
    private static async Task Capture(Window window,string root,string name)
    {
        window.UpdateLayout();await Task.Delay(150);await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,name));png.Save(file);
    }
}

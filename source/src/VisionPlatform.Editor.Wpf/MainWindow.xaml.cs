using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PixelFormat = VisionPlatform.Contracts.Images.PixelFormat;
using System.Windows.Controls.Primitives;
using VisionPlatform.Editor.Wpf.Workspace;
using Microsoft.Win32;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf.Examples;
using VisionPlatform.Editor.Wpf.Devices;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using System.Text.Json;
using VisionPlatform.AI;
using VisionPlatform.AI.Deployment;
using VisionPlatform.Editor.Wpf.AI;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Wpf;

public partial class MainWindow : Window
{
    public IEditorSession Session {get;}
    public WorkspaceViewModel Workspace {get;}
    public ExamplesViewModel Examples {get;private set;}=null!;
    public DevicesViewModel Devices {get;private set;}=null!;
    public DevicesPage DevicePage {get;private set;}=null!;
    public Task WorkspaceReady {get;}
    public AiWorkspacePage AiPage { get; private set; } = null!;
    private readonly AiWorkspaceClient _aiClient = new();
    private readonly AiServiceSettingsStore _aiSettingsStore;
    private bool _aiClosePrepared;
    private bool _closing,_closed;
    private readonly EditorWorkspaceLayoutStore _editorLayoutStore;
    private EditorWorkspaceLayout _editorLayout = new();
    private bool _layoutLoaded;
    private void FullText_Click(object sender, RoutedEventArgs e)
    {
        var text = Session.SelectedOutputInspection?.FullText;
        if (text is null) { Session.SetStatus("请选择本次执行的文本或OCR结果端口。"); return; }
        new Collections.TextDetailsWindow(text) { Owner = this }.ShowDialog();
    }
    public MainWindow(IEditorSession session) : this(session,null) { }
    public MainWindow(IEditorSession session,string? stateDirectory)
    {
        Session=session;Workspace=new(session,stateDirectory,ConfirmDiscard);
        _editorLayoutStore=new(stateDirectory);_aiSettingsStore=new(stateDirectory);InitializeComponent();DataContext=Session;
        WorkspacePages.SetupRequested += (_, _) => ShowSetup();
        WorkspacePages.PackagesRequested += (_, _) => ShowProjectPackages();
        InitializeShellCommands();
        CollectionsView.FocusRequested += roi => { Viewport.FocusRegion(roi); };
        Loaded+=async (_,_)=>{Width=Math.Min(Width,SystemParameters.WorkArea.Width);Height=Math.Min(Height,SystemParameters.WorkArea.Height);await LoadEditorLayoutAsync();};
        PreviewKeyDown+=RouteEditorKey;
        Workspace.OpenDialogAction=OpenProjectDialogAsync;
        GraphView.NodeInspectionRequested += (_, _) => InspectorTabs.SelectedIndex = 0;
        GraphView.OutputInspectionRequested += (_, _) => InspectorTabs.SelectedIndex = Session.SelectedOutputs.Any(o => o.Value is VisionPlatform.Contracts.Collections.VisionCollection or VisionPlatform.Contracts.Collections.CollectionDisplayResult or VisionPlatform.Runtime.Execution.IterationTraceStore) ? 2 : 1;
        GraphView.RunRequested += async (_, mode) => await RunCurrentAsync(mode);
        var outputView = CollectionViewSource.GetDefaultView(Session.SelectedOutputs);
        if (outputView.CanGroup && outputView.GroupDescriptions.Count == 0) outputView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OutputValueRow.Category)));
        Viewport.RoiChanged+=roi=>{try {Session.UpdateRoi(roi);}catch(Exception ex){Session.SetStatus(ex.Message);}};
        Closing+=OnClosing;
        Session.PropertyChanged+=SessionPropertyChanged;
        WorkspaceReady=InitializeWorkspaceAsync();
    }
    private async Task InitializeWorkspaceAsync()
    {
        await Workspace.InitializeAsync();
        AiServiceSettings aiSettings;
        try { aiSettings = await _aiSettingsStore.LoadAsync(); }
        catch (Exception ex) { aiSettings = AiServiceSettings.Discover(); Session.SetStatus("AI 工作区设置未恢复：" + ex.Message); }
        try
        {
            var machine = new VisionPlatform.Contracts.Deployment.MachineConfigurationStore().Load();
            if (machine.TrainingEnabled) aiSettings = aiSettings with { YoloRoot = machine.TrainingRoot, PythonPath = machine.TrainingPython };
        }
        catch (Exception ex) { Session.SetStatus("可选训练配置尚未加载：" + ex.Message); }
        AiPage = new AiWorkspacePage(Session, _aiClient, aiSettings);
        AiPage.ViewModel.LinkProjectAsync = selection =>
        {
            Session.SetAiProjectLink(new AiProjectLink(selection.ProjectId, selection.WorkspaceId, selection.DataHome));
            return Task.CompletedTask;
        };
        AiPage.ViewModel.ApplyModelAsync = ApplyAiModelAsync;
        AiPage.ViewModel.SaveSettingsAsync = settings => _aiSettingsStore.SaveAsync(settings);
        WorkspacePages.SetAiPage(AiPage);
        await AiPage.InitializeAsync();
        Examples=new(Session,Workspace.Settings.ExamplesDirectory,()=>Workspace.Settings.DefaultProjectDirectory,ConfirmDiscard,()=>FitEditorViews());
        var examplesPage = new ExamplesPage {DataContext=Examples};
        examplesPage.AiStarterRequested += (_, _) => { Navigate(WorkspacePage.AI); AiPage.OpenStarterGuide(); };
        WorkspacePages.SetExamplesPage(examplesPage);
        Devices=new(Session,ConfigureCameraNode); Devices.ConfigureDeviceNode=ConfigureCameraDeviceNode; DevicePage=new DevicesPage {DataContext=Devices}; WorkspacePages.SetDevicesPage(DevicePage);
        Workspace.PrepareOpenProject=Examples.CopyProjectPathAsync;
        Workspace.AdditionalTemplatePathCheck=path=>path.StartsWith(Path.GetFullPath(Examples.CatalogRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        Workspace.ExamplesDirectoryChanged+=ExamplesDirectoryChanged;
        // Device discovery may load a vendor SDK and send network discovery packets.
        // The explicit device-page Refresh command owns that action, never application startup.
        try {await Examples.InitializeAsync();}catch(Exception ex){Session.SetStatus("示例目录未加载："+ex.Message);}
    }
    private async void ExamplesDirectoryChanged(object? sender,EventArgs e)
    {try {if(!_closed)await Examples.ReloadAsync(Workspace.Settings.ExamplesDirectory);}catch(Exception ex){Session.SetStatus(ex.Message);}}
    private void ConfigureCameraNode(string cameraId,CameraOpenOptions options)
    {
        if(Session.IsBusy)throw new InvalidOperationException("运行中，请先停止运行再配置相机节点。");
        using var edit=Session.BeginEdit("绑定文件夹相机");
        var node=Session.SelectedNode is {TypeId:"VirtualCamera"} selected?selected:Session.Nodes.FirstOrDefault(n=>n.TypeId=="VirtualCamera");
        if(node is null){Session.AddNode("VirtualCamera",60,60);node=Session.SelectedNode!;}
        Session.CommitParameters(node,JsonDefaults.Element(new VirtualCameraParameters {CameraId=cameraId,FolderPath=options.FolderPath,
            FrameRate=options.FrameRate,Loop=options.Loop,PixelFormat=options.PixelFormat}));
        var input=Session.Nodes.FirstOrDefault(n=>n.TypeId=="ImageInput");
        if(input is not null&&!Session.Connections.Any(c=>c.Target.Node==input&&c.Target.Name=="Image"))
        {
            if(!Session.TryConnect(node.Outputs.Single(p=>p.Name=="Image"),input.Inputs.Single(p=>p.Name=="Image"),out var reason))
                throw new InvalidOperationException("相机节点参数已写入，连接ImageInput失败："+reason);
        }
        Session.SelectedNode=node;FitEditorViews();
        Session.SetStatus("相机配置已写入正式节点参数；运行流程将采集下一帧，预览帧不会冒充新检测。");
    }

    private void ConfigureCameraDeviceNode(string cameraId,string providerId,string deviceId,CameraOpenOptions options)
    {
        if(providerId==VisionPlatform.Acquisition.VirtualCameraProvider.Id){ConfigureCameraNode(cameraId,options);return;}
        if(Session.IsBusy)throw new InvalidOperationException("请先停止运行再绑定相机节点。");
        using var edit=Session.BeginEdit("绑定相机设备");
        var node=Session.SelectedNode is {TypeId:"CameraInput"} selected?selected:Session.Nodes.FirstOrDefault(n=>n.TypeId=="CameraInput");
        if(node is null){Session.AddNode("CameraInput",60,60);node=Session.SelectedNode!;}
        Session.CommitParameters(node,JsonDefaults.Element(new CameraInputParameters{CameraId=cameraId,ProviderId=providerId,DeviceId=deviceId,Options=options}));
        var input=Session.Nodes.FirstOrDefault(n=>n.TypeId=="ImageInput");
        if(input is not null&&!Session.Connections.Any(c=>c.Target.Node==input&&c.Target.Name=="Image"))
            if(!Session.TryConnect(node.Outputs.Single(p=>p.Name=="Image"),input.Inputs.Single(p=>p.Name=="Image"),out var reason))
                throw new InvalidOperationException("设备参数已写入；连接图像输入失败："+reason);
        Session.SelectedNode=node;FitEditorViews();Session.SetStatus("设备已绑定到工程节点；请连接检测流程并保存。绑定不会采集或发送数据。");
    }

    public void Navigate(WorkspacePage page)=>Workspace.CurrentPage=page;
    public async Task OpenProjectAsync(string path,bool discardChanges=false)
    {
        await WorkspaceReady;
        if(discardChanges)
        {
            var full=Path.GetFullPath(path);
            if(Workspace.IsExamplePath(full))full=await Examples.CopyProjectPathAsync(full);
            await Session.OpenAsync(full);await Workspace.RememberProjectAsync(full,Session.ProjectName);Navigate(WorkspacePage.Editor);
        }
        else await Workspace.OpenProjectAsync(path);
        FitEditorViews();
    }
    private void SessionPropertyChanged(object? sender,PropertyChangedEventArgs e)
    {if(e.PropertyName is nameof(IEditorSession.ProjectName) or nameof(IEditorSession.HasChanges))Title=$"{Session.ProjectName}{(Session.HasChanges ? " *" : "")} · {VisionPlatform.Contracts.Build.ProductBuild.Name}";}
    private async Task Execute(Func<Task> action)
    {try {await action();}catch(Exception ex){Session.SetStatus(ex.Message);}}
    private async void New_Click(object sender,RoutedEventArgs e) {await Execute(Workspace.NewProjectAsync);if(Workspace.CurrentPage==WorkspacePage.Editor)FitEditorViews();}
    private async void Open_Click(object sender,RoutedEventArgs e)=>await Execute(OpenProjectDialogAsync);
    private async Task OpenProjectDialogAsync()
    {
        var dialog=new OpenFileDialog {Title="打开 视构 VisionForge 工程",Filter="视构 VisionForge 工程 (*.json)|*.json",CheckFileExists=true,InitialDirectory=Directory.Exists(Workspace.Settings.DefaultProjectDirectory)?Workspace.Settings.DefaultProjectDirectory:null};
        if(dialog.ShowDialog(this)==true)await OpenProjectAsync(dialog.FileName);
    }
    private async void Save_Click(object sender,RoutedEventArgs e)=>await SaveProject(false);
    private async void SaveAs_Click(object sender,RoutedEventArgs e)=>await SaveProject(true);
    private async Task<bool> SaveProject(bool choosePath)
    {
        string? path=Session.ProjectPath;
        if(choosePath||path is null||Workspace.IsTemplateProject)
        {
            var dialog=new SaveFileDialog {Title="保存 视构 VisionForge 工程",Filter="视构 VisionForge 工程 (*.json)|*.json",DefaultExt=".json",FileName=Path.GetFileName(path) is {Length:>0} name ? name : "inspection.json"};
            if(dialog.ShowDialog(this)!=true)return false;path=dialog.FileName;
        }
        if(Workspace.IsExamplePath(path)){Session.SetStatus("内置示例是只读模板，请保存到默认工程目录或其他用户目录。");return false;}
        try {await Session.SaveAsync(path);await Workspace.RememberProjectAsync(path,Session.ProjectName);return true;}catch(Exception ex){Session.SetStatus(ex.Message);return false;}
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog {Title="导入检测图像",Filter="图像 (*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff)|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"};
        if(dialog.ShowDialog(this)!=true)return;
        var text=(ImportFormat.SelectedItem as ComboBoxItem)?.Content?.ToString();
        PixelFormat? format=Enum.TryParse<PixelFormat>(text,out var parsed) ? parsed : null;
        await Execute(()=>Session.ImportImageAsync(dialog.FileName,format));
    }
    private async void Demo_Click(object sender,RoutedEventArgs e)
    {
        if(!await ConfirmDiscard())return;
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VisionStudio","DemoInspection");
        await Execute(()=>Session.CreateDemoAsync(directory));
        FitEditorViews();
    }
    private async void Run_Click(object sender,RoutedEventArgs e) => await RunCurrentAsync(EditorRunMode.All);
    private async void Single_Click(object sender,RoutedEventArgs e) => await RunCurrentAsync(EditorRunMode.Single);
    private async void Prefix_Click(object sender,RoutedEventArgs e) => await RunCurrentAsync(EditorRunMode.ToSelected);
    private void Cancel_Click(object sender,RoutedEventArgs e)=>Session.Cancel();
    private void BindRoi_Click(object sender,RoutedEventArgs e) {try {Session.BindRoiToSelectedNode();}catch(Exception ex){Session.SetStatus(ex.Message);}}
    private async void Teach_Click(object sender,RoutedEventArgs e)=>await Execute(Session.TeachTemplateAsync);
    private void ResultSelection_Changed(object sender,SelectionChangedEventArgs e)
    {if((sender as DataGrid)?.SelectedItem is NodeResultRow result)Session.SelectedNode=Session.Nodes.FirstOrDefault(n=>n.NodeId==result.NodeId);}
    private void OutputSelection_Changed(object sender,SelectionChangedEventArgs e)
    {if((sender as ListBox)?.SelectedItem is OutputValueRow output)Session.SelectOutput(output);}
    private async Task<bool> ConfirmDiscard()
    {
        if(!Session.HasChanges)return true;
        var result=MessageBox.Show(this,"当前工程有未保存修改。是否先保存？","保存工程",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        return result==MessageBoxResult.No || result==MessageBoxResult.Yes && await SaveProject(false);
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_closed)return;e.Cancel=true;if(_closing)return;_closing=true;
        try
        {
            if(!await ConfirmDiscard()){_closing=false;return;}
            await CloseEditorAsync(true);
        }
        catch(Exception ex)
        {
            _closing=false;
            Session.SetStatus("尚未关闭："+ex.Message);
            MessageBox.Show(this, ex.Message, "任务尚未安全结束", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    public async Task CloseEditorAsync(bool discardChanges=false)
    {
        if(_closed)return;
        if(!discardChanges&&!await ConfirmDiscard())return;
        if (AiPage is not null && !_aiClosePrepared)
        {
            if (!await AiPage.PrepareCloseAsync(ConfirmAiCloseAsync)) { _closing = false; return; }
            _aiClosePrepared = true;
        }
        try
        {
            try {await WorkspaceReady;await Devices.StopAsync();await Examples.StopAsync();await SaveEditorLayoutAsync();await Workspace.FlushAsync();}
            finally {await Session.CloseAsync();}
        }
        finally
        {
            Session.PropertyChanged-=SessionPropertyChanged;
            Devices?.Dispose(); if(DevicePage is not null)await DevicePage.CloseAsync();
            if (AiPage is not null) await AiPage.DisposeAsync();
            await _aiClient.DisposeAsync();
            Examples?.Dispose();
            Workspace.ExamplesDirectoryChanged-=ExamplesDirectoryChanged;Workspace.Dispose();
            ParametersView.Session=null;GraphView.Dispose();
            Viewport.Dispose();await Viewport.DrainAsync();
            Session.Dispose();_closed=true;_closing=false;
            // Closing can complete synchronously. Queue the final Close so the initial Closing event
            // has returned before WPF receives another close request.
            await Dispatcher.InvokeAsync(Close,System.Windows.Threading.DispatcherPriority.Normal);
        }
    }

    private void InitializeShellCommands()
    {
        Bind(StudioCommands.New,async()=>{await Workspace.NewProjectAsync();if(Workspace.CurrentPage==WorkspacePage.Editor)FitEditorViews();});
        Bind(StudioCommands.Open,OpenProjectDialogAsync);
        Bind(StudioCommands.Save,async()=>{await SaveProject(false);});
        Bind(StudioCommands.SaveAs,async()=>{await SaveProject(true);});
        Bind(StudioCommands.Run,()=>RunCurrentAsync(EditorRunMode.All));
        Bind(StudioCommands.RunSingle,()=>RunCurrentAsync(EditorRunMode.Single),()=>Session.SelectedNode is {Specification.IsEnabled:true});
        Bind(StudioCommands.RunTo,()=>RunCurrentAsync(EditorRunMode.ToSelected),()=>Session.SelectedNode is {Specification.IsEnabled:true});
        Bind(StudioCommands.Cancel,()=>{Session.Cancel();return Task.CompletedTask;},()=>Session.IsBusy,allowBusy:true);
        void Bind(RoutedUICommand command,Func<Task> action,Func<bool>? available=null,bool allowBusy=false)
        {
            CommandBindings.Add(new CommandBinding(command,async(_,e)=>{e.Handled=true;await Execute(action);},(_,e)=>
            {e.CanExecute=!_closed&&(allowBusy||!Session.IsBusy)&&(available?.Invoke()??true);e.Handled=true;}));
        }
    }
    private async Task RunCurrentAsync(EditorRunMode mode)
    {
        await Execute(async()=>{await WorkspaceReady;await Devices.StopAsync();await Session.RunAsync(mode);});
        Navigate(WorkspacePage.Editor);InspectorTabs.SelectedIndex=1;
    }
    private void RouteEditorKey(object sender,KeyEventArgs e)
    {
        if(e.Handled||Workspace.CurrentPage!=WorkspacePage.Editor||IsTextEditing(Keyboard.FocusedElement as DependencyObject))return;
        var control=Keyboard.Modifiers==ModifierKeys.Control;
        var command=control?e.Key switch{Key.C=>"Copy",Key.V=>"Paste",Key.X=>"Cut",Key.D=>"Duplicate",Key.Z=>"Undo",Key.Y=>"Redo",Key.A=>"SelectAll",_=>null}:
            Keyboard.Modifiers==ModifierKeys.None?e.Key switch{Key.Delete=>"Delete",Key.F2=>"Rename",Key.Escape=>"Escape",_=>null}:null;
        if(command is not null)e.Handled=GraphView.RouteEditorCommand(command);
    }
    public static bool IsTextEditing(DependencyObject? target)
    {
        while(target is not null)
        {
            if(target is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)return true;
            if(target is ComboBox combo&&(combo.IsEditable||combo.IsDropDownOpen))return true;
            target=target is Visual?VisualTreeHelper.GetParent(target):LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
    private async Task LoadEditorLayoutAsync()
    {
        if(_layoutLoaded)return;
        try{_editorLayout=await _editorLayoutStore.LoadAsync();InspectorColumn.Width=new GridLength(_editorLayout.InspectorWidth);ApplyViewMode(_editorLayout.ViewMode);}
        catch(Exception ex){Session.SetStatus("编辑器布局未恢复："+ex.Message);}
        finally{_layoutLoaded=true;}
    }
    private void ApplyViewMode(string mode)
    {
        _editorLayout=_editorLayout with{ViewMode=mode};
        FlowPane.Visibility=mode=="Image"?Visibility.Collapsed:Visibility.Visible;
        ImagePane.Visibility=mode=="Flow"?Visibility.Collapsed:Visibility.Visible;
        ViewSplitter.Visibility=mode=="Both"?Visibility.Visible:Visibility.Collapsed;
        FlowRow.MinHeight=mode=="Image"?0:80;ImageRow.MinHeight=mode=="Flow"?0:80;
        MaximizeFlowButton.Content=mode=="Flow"?"恢复双视图":"放大流程";MaximizeImageButton.Content=mode=="Image"?"恢复双视图":"放大图像";
        FlowRow.Height=mode=="Image"?new GridLength(0):new GridLength(_editorLayout.FlowShare,GridUnitType.Star);
        ImageRow.Height=mode=="Flow"?new GridLength(0):new GridLength(_editorLayout.ImageShare,GridUnitType.Star);
        ViewSplitterRow.Height=new GridLength(mode=="Both"?7:0);
    }
    private async void MaximizeFlow_Click(object sender,RoutedEventArgs e){ApplyViewMode(_editorLayout.ViewMode=="Flow"?"Both":"Flow");await SaveEditorLayoutAsync();}
    private async void MaximizeImage_Click(object sender,RoutedEventArgs e){ApplyViewMode(_editorLayout.ViewMode=="Image"?"Both":"Image");await SaveEditorLayoutAsync();}
    private async void RestoreViews_Click(object sender,RoutedEventArgs e){ApplyViewMode("Both");await SaveEditorLayoutAsync();}
    private async void LayoutSplitter_DragCompleted(object sender,DragCompletedEventArgs e)=>await SaveEditorLayoutAsync();
    private async Task SaveEditorLayoutAsync()
    {
        if(!_layoutLoaded)return;
        _editorLayout=_editorLayout with{InspectorWidth=Math.Clamp(InspectorColumn.ActualWidth,240,1000)};
        if(_editorLayout.ViewMode=="Both"&&FlowRow.ActualHeight>0&&ImageRow.ActualHeight>0)
            _editorLayout=_editorLayout with{FlowShare=FlowRow.ActualHeight/ImageRow.ActualHeight,ImageShare=1};
        try{await _editorLayoutStore.SaveAsync(_editorLayout);}catch(Exception ex){Session.SetStatus("编辑器布局未保存："+ex.Message);}
    }

    public void FitEditorViews()
    {
        Navigate(WorkspacePage.Editor);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>{GraphView.FitToContent();Viewport.FitToWindow();}));
    }
}


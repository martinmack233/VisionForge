using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Communication.Tcp;
using VisionPlatform.Editor.Core.Common;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;

namespace VisionPlatform.Editor.Wpf.Examples;

/// <summary>Authoring workflow only. All algorithms are executed by the existing editor Session/Runtime.</summary>
public sealed class ExamplesViewModel : ObservableObject,IDisposable
{
    private readonly IEditorSession _session;
    private readonly Func<string> _userProjectsRoot;
    private readonly Func<Task<bool>> _confirmDiscard;
    private readonly Action _enterEditor;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource? _idle;
    private ExampleCatalog _catalog;
    private IReadOnlyList<ExampleEntry> _items=[];
    private ExampleEntry? _selected;
    private ExampleCase? _selectedCase;
    private string _search="",_category="全部",_status="选择一个离线示例查看说明；打开时创建独立工程副本。";
    private bool _busy,_disposed;
    public ExamplesViewModel(IEditorSession session,string catalogRoot,Func<string> userProjectsRoot,Func<Task<bool>> confirmDiscard,Action enterEditor)
    {
        _session=session;_userProjectsRoot=userProjectsRoot;_confirmDiscard=confirmDiscard;_enterEditor=enterEditor;
        _catalog=CreateCatalog(catalogRoot);
        _session.PropertyChanged+=SessionChanged;
        OpenCommand=new ActionCommand(()=>OpenSelectedAsync(),()=>CanInteract&&Selected is not null);
        RunCommand=new ActionCommand(()=>RunSelectedAsync(),()=>CanInteract&&Selected is not null&&!Selected.Manifest.RequiresExplicitCommunication);
        CopyCommand=new ActionCommand(()=>CopySelectedAsync(),()=>CanInteract&&Selected is not null);
        LocalTcpCommand=new ActionCommand(()=>RunSelectedAsync(true),()=>CanInteract&&Selected?.Manifest.RequiresExplicitCommunication==true);
        ReloadCommand=new ActionCommand(()=>InitializeAsync(),()=>CanInteract);
        RtuSimulationCommand=new ActionCommand(RunRtuSimulationAsync,()=>CanInteract);
        BeginnerCommand=new ActionCommand(()=>{Search="";Category=BeginnerExampleFactory.Category;Selected=VisibleItems.FirstOrDefault();Status="按01→05学习；选择示例不会执行，点击一键运行才创建副本并计算。修改副本后按F5重跑。";return Task.CompletedTask;},()=>CanInteract&&HasBeginnerExamples);
        CancelCommand=new ActionCommand(()=>{_operation?.Cancel();_session.Cancel();return Task.CompletedTask;},()=>IsBusy);
    }
    public IReadOnlyList<ExampleEntry> Items=>_items;
    public bool HasBeginnerExamples=>_items.Any(e=>e.Manifest.Category==BeginnerExampleFactory.Category);
    public bool HasGuide=>!string.IsNullOrWhiteSpace(Selected?.Manifest.Flow);
    public string CatalogRoot=>_catalog.Root;
    public bool IsBuiltInProject(string path)=>_items.Any(e=>Path.GetFullPath(path).Equals(e.ProjectPath,StringComparison.OrdinalIgnoreCase));
    public async Task<string> CopyProjectPathAsync(string projectPath)
    {
        var path=Path.GetFullPath(projectPath);
        var entry=_items.SingleOrDefault(e=>path.Equals(e.ProjectPath,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("该文件不是已注册的内置示例，请从示例中心选择完整示例。");
        return await _catalog.CopyAsync(entry,_userProjectsRoot(),ct:_lifetime.Token);
    }
    public IReadOnlyList<ExampleEntry> VisibleItems=>_items.Where(e=>(Category=="全部"||e.Manifest.Category==Category)&&
        (string.IsNullOrWhiteSpace(Search)||(e.Manifest.Name+" "+e.Manifest.Id+" "+e.Manifest.Description+" "+e.TagsText).Contains(Search,StringComparison.OrdinalIgnoreCase))).ToArray();
    public IReadOnlyList<string> Categories=>new[]{"全部"}.Concat(_items.Select(e=>e.Manifest.Category).Distinct().Order()).ToArray();
    public string Search{get=>_search;set{if(Set(ref _search,value))Notify(nameof(VisibleItems));}}
    public string Category{get=>_category;set{if(Set(ref _category,value))Notify(nameof(VisibleItems));}}
    public ExampleEntry? Selected{get=>_selected;set{if(Set(ref _selected,value)){SelectedCase=value?.Manifest.Cases.FirstOrDefault();LastVerification=null;Notify(nameof(Cases));Notify(nameof(HasCommunication));Notify(nameof(HasGuide));Notify(nameof(ExpectedText));RefreshCommands();}}}
    public IReadOnlyList<ExampleCase> Cases=>Selected?.Manifest.Cases??[];
    public ExampleCase? SelectedCase{get=>_selectedCase;set{if(Set(ref _selectedCase,value)){Notify(nameof(ExpectedText));LastVerification=null;}}}
    public string ExpectedText=>string.Join("\n",(HasCommunication?Selected?.Manifest.CommunicationExpected:SelectedCase?.Checks??Selected?.Manifest.Expected??[])
        ?.Select(c=>$"{c.NodeId}.{c.Port}{(c.Property.Length==0?"":"."+c.Property)} = {c.Expected.GetRawText()}{(c.Tolerance>0?$" ± {c.Tolerance}":"")}")??[]);
    public bool HasCommunication=>Selected?.Manifest.RequiresExplicitCommunication==true;
    public string Status{get=>_status;private set=>Set(ref _status,value);}
    public bool IsBusy{get=>_busy;private set{if(Set(ref _busy,value)){Notify(nameof(CanInteract));RefreshCommands();}}}
    public bool CanInteract=>!IsBusy;
    private ExampleVerification? _verification;
    public ExampleVerification? LastVerification{get=>_verification;private set=>Set(ref _verification,value);}
    public string? LastCopiedProjectPath{get;private set;}
    public string? LastReceivedMessage{get;private set;}
    public string? LastFailureDetails{get;private set;}
    public ICommand OpenCommand{get;} public ICommand RunCommand{get;}public ICommand CopyCommand{get;}
    public ICommand BeginnerCommand{get;}
    public ICommand RtuSimulationCommand{get;} public ICommand LocalTcpCommand{get;} public ICommand ReloadCommand{get;}public ICommand CancelCommand{get;}
    public Task InitializeAsync()=>Work(async ct=>
    {
        _items=(await _catalog.LoadAsync(ct)).OrderBy(e=>e.Manifest.Category==BeginnerExampleFactory.Category?0:1).ThenBy(e=>e.Manifest.Id,StringComparer.OrdinalIgnoreCase).ToArray();Notify(nameof(Items));Notify(nameof(VisibleItems));Notify(nameof(Categories));Notify(nameof(HasBeginnerExamples));Selected=_items.FirstOrDefault();
        Status=_catalog.LoadErrors.Count==0?$"已加载{_items.Count}个本地示例。选择示例不会执行算法。":string.Join("；",_catalog.LoadErrors);
    });
    public Task ReloadAsync(string root)
    {
        if(IsBusy){Status="示例操作进行中，取消或完成后再更改示例目录。";return Task.CompletedTask;}
        _catalog=CreateCatalog(root);Notify(nameof(CatalogRoot));return InitializeAsync();
    }
    public Task OpenSelectedAsync()=>OpenOrRun(false,false);
    public Task CopySelectedAsync()=>OpenOrRun(false,false);
    public Task RunSelectedAsync(bool allowCommunication=false)=>OpenOrRun(true,allowCommunication);
    public Task RunRtuSimulationAsync()=>Work(async ct=>{var report=await Task.Run(()=>OperatorCommunicationDemo.RunRtuSimulationAsync(ct),ct);LastReceivedMessage=JsonSerializer.Serialize(report,JsonDefaults.Options);Notify(nameof(LastReceivedMessage));LastVerification=new(report.Frames.Count==9&&!report.HardwareVerified,["实际Runtime执行8种功能码及写后读回；CRC/逐字节分帧通过；未打开COM口。"]);Status="RTU协议模拟完成（非实体硬件验收）。";});
    private Task OpenOrRun(bool run,bool allowCommunication)=>Work(async ct=>
    {
        var selected=Selected??throw new InvalidOperationException("请先选择示例。");var variant=SelectedCase;
        if(run&&selected.Manifest.RequiresExplicitCommunication&&!allowCommunication){Status="通信示例需要显式点击“启动本地通信测试”。";return;}
        if(!await _confirmDiscard())return;
        LastVerification=null;LastReceivedMessage=null;Notify(nameof(LastReceivedMessage));
        var path=await _catalog.CopyAsync(selected,_userProjectsRoot(),ct:ct);ct.ThrowIfCancellationRequested();
        if(run)await _session.OpenAsync(path);else await _session.OpenConfigurationAsync(path);
        ct.ThrowIfCancellationRequested();LastCopiedProjectPath=path;Notify(nameof(LastCopiedProjectPath));
        if(variant is not null)
        {
            var image=_session.Nodes.Single(n=>n.TypeId=="ImageInput");var p=image.Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!;
            _session.CommitParameters(image,JsonDefaults.Element(p with { Path=variant.ImagePath }));
        }
        _enterEditor();Status="已创建独立工程副本，内置模板保持只读。";
        if(!run)return;
        using var cancel=ct.Register(_session.Cancel);
        if(selected.Manifest.Id==OperatorCommunicationDemo.Ids[0]||selected.Manifest.Id==OperatorCommunicationDemo.Ids[1])
        {
            var isTcp=selected.Manifest.Id==OperatorCommunicationDemo.Ids[0];
            async Task Execute(int port,CancellationToken token)
            {var connect=_session.Nodes.Single(n=>n.NodeId=="connect");if(isTcp){var options=connect.Parameters.Deserialize<TcpCommunicationOptions>(JsonDefaults.Options)!;_session.CommitParameters(connect,JsonDefaults.Element(options with{Client=options.Client with{Host="127.0.0.1",Port=port}}));}else{var options=connect.Parameters.Deserialize<ModbusCommunicationOptions>(JsonDefaults.Options)!;_session.CommitParameters(connect,JsonDefaults.Element(options with{Tcp=options.Tcp with{Host="127.0.0.1",Port=port}}));}await _session.RunAsync();token.ThrowIfCancellationRequested();}
            var report=isTcp?await OperatorCommunicationDemo.RunTcpAsync(Execute,ct):await OperatorCommunicationDemo.RunModbusTcpAsync(Execute,ct);LastReceivedMessage=JsonSerializer.Serialize(report,JsonDefaults.Options);Notify(nameof(LastReceivedMessage));
        }
        else if(selected.Manifest.RequiresExplicitCommunication)
        {
            LastReceivedMessage=await LocalTcpExample.RunAsync(async(port,token)=>
            {
                token.ThrowIfCancellationRequested();
                var connect=_session.Nodes.Single(n=>n.TypeId=="TcpConnect");var options=connect.Parameters.Deserialize<TcpConnectParameters>(JsonDefaults.Options)!;
                _session.CommitParameters(connect,JsonDefaults.Element(options with { Options=options.Options with { Host="127.0.0.1",Port=port } }));
                var enable=_session.Nodes.Single(n=>n.NodeId=="enable");_session.CommitParameters(enable,JsonDefaults.Element(new { Value=false }));
                var send=_session.Nodes.Single(n=>n.TypeId=="TcpSend");var p=send.Parameters.Deserialize<TcpSendParameters>(JsonDefaults.Options)!;
                _session.CommitParameters(send,JsonDefaults.Element(p with { SendEnabled=true }));
                await _session.RunAsync();token.ThrowIfCancellationRequested();
            },ct);
            Notify(nameof(LastReceivedMessage));
        }
        else await _session.RunAsync();
        ct.ThrowIfCancellationRequested();
        var expected=selected.Manifest.RequiresExplicitCommunication?selected.Manifest.CommunicationExpected:variant?.Checks??selected.Manifest.Expected;
        LastVerification=ExampleVerifier.Verify(expected,_session.CurrentOutputs,_session.Results.ToDictionary(r=>r.NodeId,r=>r.Status));
        if(selected.Manifest.RequiresExplicitCommunication&&!OperatorCommunicationDemo.Ids.Contains(selected.Manifest.Id))
        {
            using var wire=JsonDocument.Parse(LastReceivedMessage!);
            var valid=wire.RootElement.GetProperty("result").GetString()=="OK"&&wire.RootElement.GetProperty("count").GetInt32()==2;
            if(!valid)LastVerification=new(false,LastVerification.Details.Append("本地TCP接收的结构化结果与预期不一致。").ToArray());
        }
        Status=LastVerification.Summary;_session.SetStatus(Status);
    });
    private ExampleCatalog CreateCatalog(string root)=>new(root,new ProjectStore(_session.Registry,new Dictionary<string,string>{["cvinspect.pattern"]="0.34.0+0eddfbaa"}));
    private async Task Work(Func<CancellationToken,Task> action)
    {
        if(_disposed||IsBusy)return;IsBusy=true;_idle=new(TaskCreationOptions.RunContinuationsAsynchronously);_operation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try{LastFailureDetails=null;await action(_operation.Token);}catch(OperationCanceledException){Status="示例操作已取消。";LastVerification=null;}
        catch(Exception ex){LastFailureDetails=ex.ToString();Status=ex.Message;LastVerification=new(false,[ex.Message]);}
        finally{_operation.Dispose();_operation=null;IsBusy=false;_idle.TrySetResult();}
    }
    private void RefreshCommands(){foreach(var command in new[]{OpenCommand,RunCommand,CopyCommand,LocalTcpCommand,ReloadCommand,CancelCommand,RtuSimulationCommand,BeginnerCommand}.OfType<ActionCommand>())command.Refresh();}
    private void SessionChanged(object? sender,PropertyChangedEventArgs e)
    {if(e.PropertyName is nameof(IEditorSession.Revision) or nameof(IEditorSession.ProjectPath)){LastVerification=null;LastReceivedMessage=null;Notify(nameof(LastReceivedMessage));}}
    public async Task StopAsync(){_lifetime.Cancel();_session.Cancel();if(_idle is not null)await _idle.Task;}
    public void Dispose(){if(_disposed)return;_disposed=true;_session.PropertyChanged-=SessionChanged;_lifetime.Cancel();_lifetime.Dispose();}
    private sealed class ActionCommand(Func<Task> action,Func<bool> canExecute):ICommand
    {
        public event EventHandler? CanExecuteChanged;public bool CanExecute(object? p)=>canExecute();
        public async void Execute(object? p){if(CanExecute(p))await action();}public void Refresh()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
    }
}

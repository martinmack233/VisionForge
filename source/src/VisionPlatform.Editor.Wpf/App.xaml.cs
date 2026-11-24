using System.Windows;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Plugins.Builtin;

namespace VisionPlatform.Editor.Wpf;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var machineIndex = Array.IndexOf(e.Args, "--machine-config");
        if (machineIndex >= 0 && machineIndex + 1 < e.Args.Length) Environment.SetEnvironmentVariable("VISIONSTUDIO_MACHINE_CONFIG", System.IO.Path.GetFullPath(e.Args[machineIndex + 1]));
        var stateIndex=Array.IndexOf(e.Args,"--workspace-state");
        var stateDirectory=stateIndex>=0&&stateIndex+1<e.Args.Length?System.IO.Path.GetFullPath(e.Args[stateIndex+1]):null;
        WorkspaceSettings settings=new();string? settingsError=null;
        try {settings=(await new WorkspaceStateStore(stateDirectory).LoadAsync()).Settings;}catch(Exception ex){settingsError=ex.Message;}
        var session=new EditorSession(BuiltinRegistration.CreateRegistry(),settings.ResultImageBudgetMiB*1048576L);
        var window=new MainWindow(session,stateDirectory);MainWindow=window;window.Show();
        try
        {
            var index=Array.IndexOf(e.Args,"--project");
            await window.WorkspaceReady;
            if(index>=0 && index+1<e.Args.Length)await window.OpenProjectAsync(System.IO.Path.GetFullPath(e.Args[index+1]));
            else {await session.NewAsync();window.Navigate(WorkspacePage.Dashboard);}
            if(settingsError is not null)session.SetStatus("使用默认工作区设置："+settingsError);
            window.ShowFirstSetupIfNeeded();
        }
        catch(Exception ex){session.SetStatus(ex.Message);}
    }
}

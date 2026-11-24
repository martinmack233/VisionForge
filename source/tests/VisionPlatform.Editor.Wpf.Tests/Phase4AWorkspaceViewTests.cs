using System.Windows;
using System.Windows.Threading;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Editor.Wpf;
using VisionPlatform.Editor.Wpf.Parameters;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase4AWorkspaceViewTests
{
    [Fact]
    public void AllPagesKeepTheOriginalEditorControlsAndUnsavedParameterDraft()
    {
        RunSta(async () =>
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisionStudio-WorkspaceView-" + Guid.NewGuid().ToString("N"));
            MainWindow? window = null;
            EditorSession? session = null;
            try
            {
                session = new EditorSession(); session.AddNode("GaussianBlur");
                window = new MainWindow(session, root); window.Show(); await window.WorkspaceReady;
                Assert.Equal(WorkspacePage.Dashboard, window.Workspace.CurrentPage);
                var panel = (ParameterPanel)window.FindName("ParametersView"); var draft = panel.Draft; Assert.NotNull(draft);
                var kernel = draft!.Fields.Single(p => p.Descriptor.Path.EndsWith("KernelSize", StringComparison.Ordinal)); kernel.Text = "9";
                var graph = window.FindName("GraphView"); var viewport = window.FindName("Viewport"); var revision = session.Revision;
                foreach (var page in Enum.GetValues<WorkspacePage>()) { window.Navigate(page); window.UpdateLayout(); }
                Assert.Same(graph, window.FindName("GraphView")); Assert.Same(viewport, window.FindName("Viewport")); Assert.Same(draft, panel.Draft);
                Assert.Equal("9", kernel.Text); Assert.Equal(revision, session.Revision);
                window.FitEditorViews(); Assert.Equal(WorkspacePage.Editor, window.Workspace.CurrentPage);
                await window.CloseEditorAsync(true); window = null;
            }
            finally
            {
                try { if (window is not null) await window.CloseEditorAsync(true); }
                finally { session?.Dispose(); }
                var resolved = System.IO.Path.GetFullPath(root); var temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(resolved).StartsWith("VisionStudio-WorkspaceView-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture.");
                if (System.IO.Directory.Exists(resolved)) System.IO.Directory.Delete(resolved, true);
            }
        });
    }
    private static void RunSta(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () => { try { await action(); } catch (Exception ex) { failure = ex; } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(40)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System.Windows;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Graph;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase3COutputInspectionTests
{
    [Fact]
    public void ReleasingOnStartingOutputCancelsConnectionWithoutFalseErrorOrStaleResults()
    {
        RunSta(() =>
        {
            using var session = new EditorSession(); session.AddNode("LogicNot");
            var node = session.SelectedNode!; session.RunAsync().GetAwaiter().GetResult();
            using var graph = new GraphEditorView { Session = session };
            var port = graph.State.AllOutputPorts.Single(p => p.Port.Node.NodeId == node.NodeId && p.Port.Name == "Result");
            var message = graph.State.ConnectionMessage;
            graph.State.StartConnectionCommand.Execute(port);
            graph.State.CompleteConnectionCommand.Execute(port);
            Assert.Equal(message, graph.State.ConnectionMessage);
            Assert.Empty(session.Connections); Assert.False(graph.State.Pending.IsVisible);
            Assert.Null(graph.State.Pending.Source); Assert.Null(graph.State.Pending.Target);
            Assert.True(graph.InspectOutput(node.NodeId, "Result"));
            Assert.Equal("True", Assert.Single(session.SelectedOutputInspection!.Fields).Value);
        });
    }

    [Fact]
    public void OutputInspectionAndNodeSelectionRemainSeparateFromDragGestures()
    {
        RunSta(() =>
        {
            Assert.True(GraphEditorView.IsInspectionClick(new(10, 20), new(10, 20)));
            Assert.False(GraphEditorView.IsInspectionClick(new(10, 20), new(300, 300)));
            using var session = new EditorSession(); session.AddNode("LogicNot"); var node = session.SelectedNode!;
            session.RunAsync().GetAwaiter().GetResult();
            using var graph = new GraphEditorView { Session = session };
            var events = 0; graph.OutputInspectionRequested += (_, _) => events++;
            Assert.True(graph.InspectOutput(node.NodeId, "Result"));
            Assert.Equal(1, events); Assert.Equal("True", Assert.Single(session.SelectedOutputInspection!.Fields).Value);
            Assert.Same(node, session.SelectedNode);
            Assert.False(graph.InspectOutput(node.NodeId, "missing")); Assert.Null(session.SelectedOutputInspection);
        });
    }
    private static void RunSta(Action action)
    {
        Exception? failure = null; var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

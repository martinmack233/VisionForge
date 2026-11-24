using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Graph;
using Xunit;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class Phase5GraphEditingTests
{
    [Fact]
    public void MultiSelectionRemainsIndependentOfInspectorAndPasteSelectsEntireSubgraph()
    {
        using var session = new EditorSession(); session.AddNode("MedianBlur"); session.AddNode("Threshold");
        using var graph = new GraphEditorState(multiSelection: true); graph.Attach(session);
        graph.SelectAllCommand.Execute(null); Assert.Equal(2, graph.SelectedNodes.Count);
        session.SelectedNode = session.Nodes[0]; Assert.Equal(2, graph.SelectedNodes.Count);
        graph.CopyCommand.Execute(null); graph.PasteCommand.Execute(null);
        Assert.Equal(4, graph.Nodes.Count); Assert.Equal(2, graph.SelectedNodes.Count);
        graph.UndoCommand.Execute(null); Assert.Equal(2, graph.Nodes.Count);
        graph.RedoCommand.Execute(null); Assert.Equal(4, graph.Nodes.Count);
    }

    [Fact]
    public void NodifyDragCommandsCommitOneMovementHistoryEntry()
    {
        using var session = new EditorSession(); session.AddNode("MedianBlur");
        using var graph = new GraphEditorState(multiSelection: true); graph.Attach(session);
        graph.DragStartedCommand.Execute(null);
        for (var i = 1; i <= 30; i++) graph.Nodes[0].Location = new(i, i);
        graph.DragCompletedCommand.Execute(null);
        graph.UndoCommand.Execute(null); Assert.Equal(0, graph.Nodes[0].Location.X);
        graph.RedoCommand.Execute(null); Assert.Equal(30, graph.Nodes[0].Location.X);
    }

    [Fact]
    public async Task LoadedGraphContextMenuUsesActualDisableAndUndoCommandsAndTextFocusIsReserved()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                using var session = new EditorSession(); session.AddNode("MedianBlur"); session.AddNode("Threshold");
                using var graph = new GraphEditorView { Session = session };
                var window = new Window { Width = 950, Height = 600, Content = graph, ShowInTaskbar = false };
                try
                {
                    window.Show(); graph.UpdateLayout();
                    graph.RouteEditorCommand("SelectAll"); graph.RouteEditorCommand("Copy"); graph.RouteEditorCommand("Paste");
                    Assert.Equal(4, session.Nodes.Count); Assert.Equal(2, graph.State.SelectedNodes.Count);
                    var target = session.SelectedNode!; graph.RouteEditorCommand("ContextMenu");
                    var menu = Assert.IsType<ContextMenu>(graph.ActiveContextMenu); Assert.True(menu.IsOpen);
                    var disable = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "禁用算子"));
                    disable.Command.Execute(null); Assert.False(target.IsEnabled); Assert.Equal("已禁用", target.Status);
                    graph.RouteEditorCommand("Undo"); Assert.True(session.Nodes.Single(n => n.NodeId == target.NodeId).IsEnabled);
                    var text = new TextBox { Text = "参数文字" }; Assert.True(GraphEditorView.IsTextEditingFocus(text));
                    Assert.True(GraphEditorView.IsTextEditingFocus(new ComboBox { IsEditable = true }));
                    Assert.False(GraphEditorView.IsTextEditingFocus(graph));
                }
                finally { window.Close(); }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

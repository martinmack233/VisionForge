using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Graph;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;

public sealed class GraphEditorTests
{
    [Fact]
    public void GraphAcceptsOnlyConnectionsApprovedBySessionAndTracksDisconnection()
    {
        var session = DispatchProxy.Create<IEditorSession, SessionProbe>();
        var probe = (SessionProbe)(object)session;
        var source = Node("source", [], [new("Image", typeof(string))]);
        var target = Node("target", [new("Image", typeof(string))], []);
        probe.Nodes.Add(source); probe.Nodes.Add(target);
        using var state = new GraphEditorState(); state.Attach(session);
        var output = Assert.Single(state.OutputPorts); var input = Assert.Single(state.InputPorts);
        Assert.False(state.Connect(output, input));
        Assert.Empty(state.Connections); Assert.False(input.IsConnected);
        Assert.Contains("拒绝", state.ConnectionMessage);
        probe.AllowConnection = true;
        Assert.True(state.Connect(input, output)); // Dragging can start from either direction.
        Assert.Equal(2, probe.ConnectionAttempts);
        var connection = Assert.Single(state.Connections);
        Assert.Same(source, connection.Connection.Source.Node);
        Assert.True(input.IsConnected); Assert.True(output.IsConnected);
        state.Disconnect(connection);
        Assert.Empty(state.Connections); Assert.False(input.IsConnected); Assert.False(output.IsConnected);
    }

    [Fact]
    public void ViewSelectionAndMovementReachSessionAndDetachmentRemovesSubscriptions()
    {
        var session = DispatchProxy.Create<IEditorSession, SessionProbe>();
        var probe = (SessionProbe)(object)session;
        var core = Node("one", [], []); probe.Nodes.Add(core);
        var state = new GraphEditorState(); state.Attach(session);
        var node = Assert.Single(state.Nodes);
        node.IsSelected = true; Assert.Same(core, probe.Selected);
        node.Location = new(123.5, 67.25);
        Assert.Equal(1, probe.MoveCount); Assert.Equal(123.5, core.X); Assert.Equal(67.25, core.Y);
        Assert.Equal(1, probe.Subscribers);
        state.Dispose(); Assert.Equal(0, probe.Subscribers);
        probe.Nodes.Add(Node("later", [], [])); Assert.Empty(state.Nodes);
    }

    [Fact]
    public void DeselectionClearsCurrentNodeWithoutClearingANewerSelection()
    {
        var session = DispatchProxy.Create<IEditorSession, SessionProbe>();
        var probe = (SessionProbe)(object)session;
        var first = Node("first", [], []); var second = Node("second", [], []);
        probe.Nodes.Add(first); probe.Nodes.Add(second);
        using var state = new GraphEditorState(); state.Attach(session);
        var a = state.Nodes[0]; var b = state.Nodes[1];
        a.IsSelected = true; Assert.Same(first, session.SelectedNode);
        b.IsSelected = true; Assert.Same(second, session.SelectedNode);
        Assert.False(a.IsSelected); Assert.True(b.IsSelected);
        a.IsSelected = false; Assert.Same(second, session.SelectedNode);
        b.IsSelected = false; Assert.Null(session.SelectedNode);
        session.SelectedNode = first; Assert.True(a.IsSelected); Assert.False(b.IsSelected);
    }

    [Fact]
    public void PendingDragUsesActualNodifyCommandParametersAndClearsCancelledOrDetachedState()
    {
        var session = DispatchProxy.Create<IEditorSession, SessionProbe>();
        var probe = (SessionProbe)(object)session; probe.AllowConnection = true;
        probe.Nodes.Add(Node("source", [], [new("Image", typeof(string))]));
        probe.Nodes.Add(Node("target", [new("Image", typeof(string))], []));
        using var state = new GraphEditorState(); state.Attach(session);
        var source = Assert.Single(state.OutputPorts); var target = Assert.Single(state.InputPorts);
        state.StartConnectionCommand.Execute(source);
        Assert.True(state.Pending.IsVisible);
        state.CompleteConnectionCommand.Execute(target);
        Assert.Single(state.Connections); Assert.False(state.Pending.IsVisible);
        Assert.Null(state.Pending.Source); Assert.Null(state.Pending.Target);
        state.StartConnectionCommand.Execute(source); state.CompleteConnectionCommand.Execute(null);
        Assert.Equal(1, probe.ConnectionAttempts); Assert.Single(state.Connections);
        state.StartConnectionCommand.Execute(source); state.Attach(null);
        Assert.False(state.Pending.IsVisible); Assert.Null(state.Pending.Source);
    }

    [Fact]
    public void NodeTitleUsesCatalogLabelWithoutChangingStoredNameAndKeepsUserNames()
    {
        var session = DispatchProxy.Create<IEditorSession, SessionProbe>();
        var specification = new NodeSpecification("blur", "GaussianBlur", "GaussianBlur", JsonDefaults.Element(new { }));
        var node = new EditorNode(specification, new("GaussianBlur", "1.0.0", [], []));
        using var visual = new GraphNodeViewModel(node, session);
        Assert.Equal("高斯滤波", visual.DisplayName);
        Assert.Equal("GaussianBlur", node.Specification.Name);
        node.ApplySpecification(specification with { Name = "定位图降噪" });
        Assert.Equal("定位图降噪", visual.DisplayName);
    }

    private static EditorNode Node(string id, PortDefinition[] inputs, PortDefinition[] outputs)
        => new(new(id, "test", id, JsonDefaults.Element(new { })), new("test", "1.0.0", inputs, outputs));

    public class SessionProbe : DispatchProxy
    {
        public ObservableCollection<EditorNode> Nodes { get; } = [];
        public ObservableCollection<EditorConnection> Connections { get; } = [];
        public NodeAuthoringCatalog Catalog { get; } = new();
        public EditorNode? Selected { get; private set; }
        private PropertyChangedEventHandler? _changed;
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public bool AllowConnection { get; set; }
        public int ConnectionAttempts { get; private set; }
        public int MoveCount { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            switch (method?.Name)
            {
                case "get_Nodes": return Nodes;
                case "get_Connections": return Connections;
                case "get_Catalog": return Catalog;
                case "get_SelectedNode": return Selected;
                case "set_SelectedNode": Selected = (EditorNode?)args[0]; _changed?.Invoke(this, new(nameof(IEditorSession.SelectedNode))); return null;
                case "add_PropertyChanged": _changed += (PropertyChangedEventHandler?)args[0]; return null;
                case "remove_PropertyChanged": _changed -= (PropertyChangedEventHandler?)args[0]; return null;
                case "MoveNode":
                    MoveCount++; var node = (EditorNode)args[0]!; node.X = (double)args[1]!; node.Y = (double)args[2]!; return null;
                case "TryConnect":
                    ConnectionAttempts++; args[2] = AllowConnection ? null : "会话拒绝此连接";
                    if (AllowConnection) Connections.Add(new((EditorPort)args[0]!, (EditorPort)args[1]!));
                    return AllowConnection;
                case "Disconnect": Connections.Remove((EditorConnection)args[0]!); return null;
                case "SetStatus": return null;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
}

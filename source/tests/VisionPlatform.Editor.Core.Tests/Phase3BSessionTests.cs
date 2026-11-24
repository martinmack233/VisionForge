using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Results;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase3BSessionTests
{
    [Fact]
    public async Task SingleMergeUsesOneFreshControlSnapshotAndNeverReusesInactiveBranch()
    {
        var calls = 0; var registry = BuiltinRegistration.CreateRegistry();
        registry.Register(() => new ProbeNode(() => calls++));
        using var session = new EditorSession(registry);
        session.Catalog.Register<ProbeOptions>("DecisionProbe", "条件测试", "逻辑控制");
        var root = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase3B-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var project = new ProjectDocument { Nodes = [Node("source", "DecisionProbe", new { Value = true }), Node("if", "IfElse", new { }),
                Node("yes", "LogicNot", new { Value = true }), Node("no", "LogicNot", new { Value = false }), Node("merge", "Merge", new { })],
                Connections = [new("source", "Result", "if", "Condition"), new("if", "True", "yes", NodePorts.Execute), new("if", "False", "no", NodePorts.Execute),
                    new("yes", NodePorts.Completed, "merge", "TrueControl"), new("yes", "Result", "merge", "TrueValue"),
                    new("no", NodePorts.Completed, "merge", "FalseControl"), new("no", "Result", "merge", "FalseValue")] };
            var path = Path.Combine(root, "branch.json"); await new ProjectStore(registry).SaveAsync(path, project);
            await session.OpenAsync(path); session.SelectedNode = session.Nodes.Single(n => n.NodeId == "merge");
            await session.RunAsync(EditorRunMode.Single);
            Assert.Equal(1, calls); Assert.Single(session.Results); Assert.Equal("Success", session.Results[0].Status);
            Assert.False((bool)session.SelectedOutputs.Single(o => o.PortName == "Boolean").Value);
            Assert.Equal("True", session.SelectedOutputs.Single(o => o.PortName == "ActiveBranch").Value);
            session.CommitParameters(session.Nodes.Single(n => n.NodeId == "source"), JsonDefaults.Element(new { Value = false }));
            Assert.Empty(session.SelectedOutputs);
            await session.RunAsync(EditorRunMode.Single);
            Assert.Equal(2, calls); Assert.True((bool)session.SelectedOutputs.Single(o => o.PortName == "Boolean").Value);
            Assert.Equal("False", session.SelectedOutputs.Single(o => o.PortName == "ActiveBranch").Value);
            session.SelectedNode = session.Nodes.Single(n => n.NodeId == "yes");
            await session.RunAsync(EditorRunMode.Single);
            Assert.Equal("Skipped", Assert.Single(session.Results).Status); Assert.Empty(session.SelectedOutputs);
            var saved = Path.Combine(root, "saved.json"); await session.SaveAsync(saved); await session.OpenAsync(saved);
            Assert.Equal(ProjectDocument.CurrentSchemaVersion, session.SnapshotProject().SchemaVersion); Assert.Equal(project.Connections, session.SnapshotProject().Connections);
        }
        finally { RemoveFixture(root); }
    }

    [Fact]
    public async Task ProjectSwitchAndCloseReleaseListenerAndOpeningOrEditingDoesNotStartIt()
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var session = new EditorSession(); session.AddNode("TcpServerStart");
        session.CommitParameters(session.SelectedNode!, JsonDefaults.Element(new { ServerId = "project-listener", Options = new { Port = port } }));
        CanBind(port); // Authoring parameters must not listen.
        var root = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase3B-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "server.json"); await session.SaveAsync(path); await session.RunAsync();
            Assert.Equal("Success", Assert.Single(session.Results).Status);
            var occupied = new TcpListener(IPAddress.Loopback, port);
            try { Assert.Throws<SocketException>(() => occupied.Start()); } finally { occupied.Stop(); }
            await session.NewAsync(); CanBind(port);
            await session.OpenAsync(path); Assert.Empty(session.Results); CanBind(port);
            await session.RunAsync(); await session.CloseAsync(); CanBind(port);
        }
        finally { RemoveFixture(root); }
    }

    [Fact]
    public async Task StructuredResultBindingInvalidatesOnEditAndProjectSwitch()
    {
        using var session = new EditorSession(); session.AddNode("LogicNot"); var boolean = session.SelectedNode!;
        session.AddNode("ResultDisplay"); var display = session.SelectedNode!;
        Assert.True(session.TryConnect(boolean.Outputs.Single(p => p.Name == "Result"), display.Inputs.Single(p => p.Name == "IsOk"), out _));
        await session.RunAsync(); Assert.True(session.CurrentInspectionReport!.IsOk);
        Assert.NotEqual(Guid.Empty, session.CurrentInspectionReport.InspectionId);
        session.CommitParameters(boolean, JsonDefaults.Element(new { Value = true })); Assert.Null(session.CurrentInspectionReport);
        await session.RunAsync(); Assert.False(session.CurrentInspectionReport!.IsOk);
        await session.NewAsync(); Assert.Null(session.CurrentInspectionReport);
    }

    private static NodeSpecification Node(string id, string type, object options) => new(id, type, type, JsonDefaults.Element(options));
    private static void CanBind(int port) { var listener = new TcpListener(IPAddress.Loopback, port); try { listener.Start(); } finally { listener.Stop(); } }
    private static void RemoveFixture(string root)
    {
        var target = Path.GetFullPath(root); var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("VisionStudio-Phase3B-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        Directory.Delete(target, recursive: true);
    }
    public sealed record ProbeOptions { public bool Value { get; init; } }
    private sealed class ProbeNode(Action called) : NodeBase<ProbeOptions>
    {
        public override NodeDescriptor Descriptor => Describe("DecisionProbe", [], [new("Result", typeof(bool))]);
        public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        { called(); return ValueTask.FromResult(new NodeOutput(new Dictionary<string, object> { ["Result"] = context.Parameters<ProbeOptions>().Value })); }
    }
}

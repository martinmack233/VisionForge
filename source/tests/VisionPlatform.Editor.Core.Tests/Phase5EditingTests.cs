using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Contracts.Acquisition;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Imaging;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase5EditingTests
{
    [Fact]
    public void DisableDisconnectsDataAndControlAndUndoRestoresButEnableDoesNot()
    {
        using var editor = new EditorSession();
        editor.AddNode("MedianBlur"); var a = editor.SelectedNode!;
        editor.AddNode("Threshold"); var b = editor.SelectedNode!;
        Assert.True(editor.TryConnect(a.Outputs.Single(p => p.Name == "ProcessedImage"), b.Inputs.Single(p => p.Name == "Image"), out _));
        Assert.True(editor.TryConnect(a.AllOutputs.Single(p => p.Name == NodePorts.Completed), b.AllInputs.Single(p => p.Name == NodePorts.Execute), out _));
        var aId = a.NodeId; var original = a.Parameters.GetRawText();
        editor.SetNodeEnabled(a, false);
        Assert.False(a.IsEnabled); Assert.Equal("已禁用", a.Status); Assert.Empty(editor.Connections);
        Assert.False(editor.TryConnect(a.Outputs[0], b.Inputs.Single(p => p.Name == "Image"), out var error)); Assert.Contains("禁用", error);
        editor.Undo();
        a = editor.Nodes.Single(n => n.NodeId == aId);
        Assert.True(a.IsEnabled); Assert.Equal(2, editor.Connections.Count);
        editor.Redo(); a = editor.Nodes.Single(n => n.NodeId == aId);
        Assert.False(a.IsEnabled); Assert.Empty(editor.Connections);
        editor.SetNodeEnabled(a, true);
        Assert.True(a.IsEnabled); Assert.Empty(editor.Connections); Assert.Equal(original, a.Parameters.GetRawText());
    }

    [Fact]
    public void SubgraphPasteUsesNewIdsAndInternalConnectionsAndOneUndoEntry()
    {
        using var editor = new EditorSession();
        editor.AddNode("MedianBlur", 10, 20); var a = editor.SelectedNode!;
        editor.AddNode("Threshold", 400, 30); var b = editor.SelectedNode!;
        Assert.True(editor.TryConnect(a.Outputs.Single(p => p.Name == "ProcessedImage"), b.Inputs.Single(p => p.Name == "Image"), out _));
        var clip = editor.CopyNodes([a, b]); var pasted = editor.PasteNodes(clip);
        Assert.Equal(4, editor.Nodes.Count); Assert.Equal(2, editor.Connections.Count);
        Assert.All(pasted, n => Assert.DoesNotContain(n.NodeId, clip.Nodes.Select(s => s.NodeId)));
        var copyA = pasted.Single(n => n.TypeId == a.TypeId); Assert.Equal(50, copyA.X); Assert.Equal(60, copyA.Y);
        Assert.Equal(a.Parameters.GetRawText(), copyA.Parameters.GetRawText());
        var pastedIds = pasted.Select(n => n.NodeId).ToHashSet();
        Assert.Single(editor.Connections, c => pastedIds.Contains(c.Source.Node.NodeId) && pastedIds.Contains(c.Target.Node.NodeId));
        Assert.Empty(editor.CurrentOutputs); Assert.All(pasted, n => Assert.Equal("未运行", n.Status));
        editor.Undo(); Assert.Equal(2, editor.Nodes.Count); Assert.Single(editor.Connections);
        editor.Redo(); Assert.Equal(4, editor.Nodes.Count); Assert.Equal(2, editor.Connections.Count);
    }

    [Fact]
    public void MoveGestureCoalescesAndParameterRenameAndRoiAreUndoable()
    {
        using var editor = new EditorSession(); editor.AddNode("MedianBlur", 0, 0); var node = editor.SelectedNode!; var id = node.NodeId;
        using (editor.BeginEdit("拖动"))
            for (var i = 1; i <= 20; i++) editor.MoveNode(node, i, i * 2);
        editor.Undo(); node = editor.Nodes.Single(n => n.NodeId == id); Assert.Equal(0, node.X); Assert.Equal(0, node.Y);
        editor.Redo(); node = editor.Nodes.Single(n => n.NodeId == id); Assert.Equal(20, node.X); Assert.Equal(40, node.Y);
        var old = node.Parameters.GetRawText(); editor.CommitParameters(node, JsonDefaults.Element(new { KernelSize = 7 }));
        editor.Undo(); node = editor.Nodes.Single(n => n.NodeId == id); Assert.Equal(old, node.Parameters.GetRawText());
        editor.RenameNode(node, "后处理"); editor.Undo(); Assert.NotEqual("后处理", editor.Nodes.Single().Name);
        editor.UpdateRoi(new(40, 40, 12, 16, 20)); Assert.Single(editor.SnapshotProject().ROI);
        editor.Undo(); Assert.Empty(editor.SnapshotProject().ROI);
    }

    [Fact]
    public void CrossProjectRelativeResourcesAreExplicitlyRejectedButPlainNodesCanPaste()
    {
        using var source = new EditorSession(); using var target = new EditorSession();
        source.AddNode("ImageInput"); var image = source.SelectedNode!;
        source.CommitParameters(image, JsonDefaults.Element(new { Path = "images/sample.png" }));
        var rejection = Assert.Throws<InvalidOperationException>(() => target.PasteNodes(source.CopyNodes([image])));
        Assert.Contains("跨工程", rejection.Message); Assert.Empty(target.Nodes);
        source.AddNode("LogicNot"); var logic = source.SelectedNode!;
        Assert.Single(target.PasteNodes(source.CopyNodes([logic])));
    }

    [Fact]
    public async Task DisabledNodeCannotRunIndividuallyAndNewProjectClearsHistory()
    {
        using var editor = new EditorSession(); editor.AddNode("LogicNot"); var node = editor.SelectedNode!;
        editor.SetNodeEnabled(node, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.RunAsync(EditorRunMode.Single));
        Assert.Empty(editor.CurrentOutputs);
        await editor.NewAsync(); Assert.False(editor.CanUndo); Assert.False(editor.CanRedo); Assert.Empty(editor.Nodes);
    }

    [Fact]
    public async Task FrozenFrameUsesSameRuntimeWithoutOpeningCameraAndMaintainsExactFrameIdentity()
    {
        var initial = FrameHandle.ActiveBytes;
        using (var frame = FrameFactory.CopyFrom(Enumerable.Repeat((byte)220, 16 * 16).ToArray(), 16, 16, PixelFormat.Mono8))
        using (var editor = new EditorSession())
        {
            editor.AddNode("VirtualCamera"); var source = editor.SelectedNode!;
            editor.AddNode("BlobDetection"); var blob = editor.SelectedNode!;
            Assert.True(editor.TryConnect(source.Outputs.Single(p => p.Name == "Image"), blob.Inputs.Single(p => p.Name == "Image"), out _));
            var info = new CameraFrameInfo(frame.FrameId, 17, frame.Timestamp, frame.Width, frame.Height, frame.Stride, frame.PixelFormat, "frozen-test");
            await editor.RunWithFrameAsync(source.NodeId, frame, info);
            Assert.All(editor.Results, row => Assert.Equal("Success", row.Status));
            Assert.Equal(frame.FrameId, ((ImageFrame)editor.CurrentOutputs[source.NodeId]["Image"]).FrameId);
            Assert.Equal(17, ((CameraFrameInfo)editor.CurrentOutputs[source.NodeId]["FrameInfo"]).Sequence);
            Assert.Equal(1, editor.CurrentOutputs[blob.NodeId]["BlobCount"]);
            Assert.False(frame.IsDisposed); Assert.Same(frame.BufferHandle, ((ImageFrame)editor.CurrentOutputs[source.NodeId]["Image"]).BufferHandle);
            await Assert.ThrowsAsync<ArgumentException>(() => editor.RunWithFrameAsync(source.NodeId, frame, info with { FrameId = Guid.NewGuid() }));
            editor.CommitParameters(blob, blob.Parameters); Assert.Empty(editor.CurrentOutputs); Assert.False(frame.IsDisposed);
        }
        Assert.Equal(initial, FrameHandle.ActiveBytes);
    }

    [Fact]
    public async Task DisabledTemplateCopyAndUndoPreserveFormalResourcesAndRoi()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VisionStudio-Phase5-EditResources-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var editor = new EditorSession(); await editor.CreateDemoAsync(directory);
            var original = editor.SnapshotProject(); var match = editor.Nodes.Single(n => n.TypeId == "TemplateMatching");
            editor.SetNodeEnabled(match, false); var clip = editor.CopyNodes([match]);
            Assert.Empty(clip.Connections); Assert.Single(clip.Templates);
            var pasted = Assert.Single(editor.PasteNodes(clip)); Assert.False(pasted.IsEnabled);
            Assert.Equal(match.Parameters.GetRawText(), pasted.Parameters.GetRawText());
            Assert.DoesNotContain(editor.Connections, e => e.Source.Node == pasted || e.Target.Node == pasted);
            Assert.Equal(original.ROI, editor.SnapshotProject().ROI);
            editor.Undo(); editor.Undo(); var restored = editor.SnapshotProject();
            Assert.Equal(original.TemplateResources, restored.TemplateResources); Assert.Equal(original.ROI, restored.ROI);
            Assert.Equal(original.Connections, restored.Connections); Assert.True(restored.Nodes.Single(n => n.NodeId == match.NodeId).IsEnabled);
        }
        finally { if (Directory.Exists(directory) && Path.GetFileName(directory).StartsWith("VisionStudio-Phase5-EditResources-", StringComparison.Ordinal)) Directory.Delete(directory, true); }
    }
}

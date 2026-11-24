using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Wpf.Parameters;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using Xunit;

namespace VisionPlatform.Editor.Wpf.Tests;
public sealed class ReleasePrepParameterTests
{
    [Fact]
    public void DisplaySelectionOrderPrecisionAndApplyUseTheExistingDraft() => Sta(async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "release-prep-" + Guid.NewGuid().ToString("N")); await CollectionExampleFactory.SaveAllAsync(folder);
        using var session = new EditorSession(); await session.OpenAsync(Path.Combine(folder, "collection-blob-measure/project.json")); await session.RunAsync();
        var node = session.Nodes.Single(n => n.NodeId == "display"); var original = node.Parameters.Deserialize<CollectionDisplayOptions>(JsonDefaults.Options)!;
        const double precise = .851234567890123; session.CommitParameters(node, JsonDefaults.Element(original with { BackgroundOpacity = precise, Fields = "State,Index,Number" })); await session.RunAsync(); session.SelectedNode = node;
        var panel = new ParameterPanel { Session = session }; var window = Show(panel);
        try
        {
            var selector = Find<DisplayFieldSelector>(panel).Single(); Assert.Contains("Number", selector.AvailableKeys); Assert.DoesNotContain("Class", selector.AvailableKeys);
            Assert.True(panel.Draft!.TryBuild(out var untouched, out _)); Assert.Equal(precise, untouched.GetProperty("BackgroundOpacity").GetDouble());
            var move = Find<Button>(selector).First(b => Equals(b.ToolTip, "向后显示")); move.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.StartsWith("Index,State", panel.Draft.Fields.Single(f => f.Descriptor.Path == "Fields").Text);
            Assert.NotEmpty(session.CurrentOutputs); Assert.True(panel.ApplyChanges()); Assert.Empty(session.CurrentOutputs);
            Assert.Equal(precise, node.Parameters.GetProperty("BackgroundOpacity").GetDouble()); Assert.StartsWith("Index,State", node.Parameters.GetProperty("Fields").GetString());
            await session.SaveAsync(Path.Combine(folder, "collection-blob-measure/saved.json")); await session.OpenAsync(Path.Combine(folder, "collection-blob-measure/saved.json")); Assert.Equal(precise, session.Nodes.Single(n => n.NodeId == "display").Parameters.GetProperty("BackgroundOpacity").GetDouble());
            var reopened = session.Nodes.Single(n => n.NodeId == "display"); session.SelectedNode = reopened;
            session.Disconnect(session.Connections.Single(e => e.Target.Node == reopened && e.Target.Name == "Collection"));
            var sort = session.Nodes.Single(n => n.NodeId == "sort"); Assert.True(session.TryConnect(sort.Outputs.Single(p => p.Name == "Collection"), reopened.Inputs.Single(p => p.Name == "Collection"), out var reason), reason);
            var updated = Find<DisplayFieldSelector>(panel).Single(); Assert.Contains("Area", updated.AvailableKeys); Assert.DoesNotContain("Number", updated.AvailableKeys);
        }
        finally { window.Close(); }
    });
    [Fact]
    public void ChineseEnumLabelsRetainStableKeysAndColorManualInputIsPreserved() => Sta(() =>
    {
        var catalog = NodeAuthoringCatalogForTest(); var draft = new ParameterDraft(catalog, JsonDefaults.Element(new CollectionDisplayOptions()));
        var position = draft.Fields.Single(f => f.Descriptor.Path == "Position"); Assert.Contains(EnumPresentation.Choices(position.Descriptor), c => c.Key == "Above" && c.Label == "目标上方");
        var field = draft.Fields.Single(f => f.Descriptor.Path == "TextColor"); var editor = new ColorParameterEditor(field); var window = Show(editor);
        try { var text = Find<TextBox>(editor).Single(); text.Text = "#123ABC"; Assert.Equal("#123ABC", field.Text); text.Text = "invalid"; Assert.Equal("invalid", field.Text); Assert.False(ColorParameterEditor.TryColor(field.Text, out _)); Assert.False(new CollectionDisplayNode().ValidateParameters(JsonDefaults.Element(new CollectionDisplayOptions { TextColor = field.Text })).Count == 0); }
        finally { window.Close(); } return Task.CompletedTask;
    });
    [Fact]
    public void MissingSubflowInputsAndCollectionOutputsHaveNodeLocations()
    {
        var registry = BuiltinRegistration.CreateRegistry(); var body = CollectionSubflows.Ocr();
        body = body with { Connections = body.Connections.Where(c => c.TargetPort != "Image" && c.TargetPort != "OCR").ToList() };
        var problems = SubflowProblems.Find(registry, body);
        Assert.Contains(problems, p => p.NodeId == "ocr" && p.Port == "Image"); Assert.Contains(problems, p => p.NodeId == "collect" && p.Message.Contains("收集"));
    }
    private static VisionPlatform.Editor.Core.Catalog.NodeAuthoringDefinition NodeAuthoringCatalogForTest() => new VisionPlatform.Editor.Core.Catalog.NodeAuthoringCatalog().Get("CollectionDisplay");
    private static Window Show(object content) { var window = new Window { Content = content, Width = 500, Height = 700, Left = -20000, ShowInTaskbar = false }; window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/VisionStudio;component/Themes/LightBlue.xaml") }); window.Show(); window.UpdateLayout(); return window; }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject { if (parent is T current) yield return current; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(parent, i))) yield return child; }
    private static void Sta(Func<Task> action) { Exception? failure = null; var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await action(); } catch (Exception ex) { failure = ex; } finally { frame.Continue = false; } }); Dispatcher.PushFrame(frame); }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "STA action timed out"); if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
}

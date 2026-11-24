using System.ComponentModel;
using System.Text.Json;
using VisionPlatform.AI;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Runtime.Projects;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using Xunit;

namespace VisionPlatform.AI.Tests;

public sealed class AssociationTests
{
    [Fact]
    public async Task FormalAssociationSurvivesSaveReloadAndUndoWithoutDeletingData()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionStudio-AI-Link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var marker = Path.Combine(root, "dataset-must-survive.txt"); await File.WriteAllTextAsync(marker, "owned by AI workspace");
        try
        {
            var imagePath = Path.Combine(root, "input.png");
            using (var pixels = new ImageFrameBuilder(8, 8, PixelFormat.Mono8))
            using (var frame = pixels.Build()) ImageFile.Save(frame, imagePath);
            using var session = new EditorSession(); session.AddNode("ImageInput");
            session.CommitParameters(session.SelectedNode!, JsonDefaults.Element(new ImageInputParameters { Path = imagePath }));
            var link = new AiProjectLink("independent-ai-project", "independent-workspace", root);
            session.SetAiProjectLink(link); Assert.Equal(link, session.SnapshotProject().AiProject);
            session.Undo(); Assert.Null(session.SnapshotProject().AiProject); Assert.True(File.Exists(marker));
            session.Redo(); Assert.Equal(link, session.SnapshotProject().AiProject);
            var path = Path.Combine(root, "inspection.json"); await session.SaveAsync(path);
            await session.OpenConfigurationAsync(path); Assert.Equal(link, session.SnapshotProject().AiProject);
            Assert.Equal(ProjectDocument.CurrentSchemaVersion, session.SnapshotProject().SchemaVersion);
            await session.NewAsync(); Assert.Null(session.SnapshotProject().AiProject); Assert.True(File.Exists(marker));
            await session.CloseAsync();
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void HiddenDeploymentMetadataIsPreservedWhenVisibleParametersAreApplied()
    {
        var definition = new NodeAuthoringDefinition("hidden", "hidden", "test", typeof(HiddenParameters), JsonDefaults.Element(new HiddenParameters()));
        var draft = new ParameterDraft(definition, JsonDefaults.Element(new HiddenParameters { SecretPin = "deployment-hash", Threshold = .4 }));
        Assert.DoesNotContain(draft.Fields, p => p.Descriptor.Path == "SecretPin");
        Assert.Single(draft.Fields).Text = "0.7";
        Assert.True(draft.TryBuild(out var parameters, out _));
        Assert.Equal("deployment-hash", parameters.GetProperty("SecretPin").GetString()); Assert.Equal(.7, parameters.GetProperty("Threshold").GetDouble());
    }
    [Fact]
    public async Task MissingPythonDoesNotStartOrInstallAnything()
    {
        await using var client = new AiWorkspaceClient();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.StartAsync(new AiServiceSettings { PythonPath = "missing.exe" }));
        Assert.Contains("Python", error.Message); Assert.False(client.IsRunning);
    }
    [Fact]
    public void DataHomeCannotBeTheProgramRootOrItsDescendant()
    {
        foreach (var path in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "user-data") })
            Assert.Contains(new AiServiceSettings { DataHome = path }.Validate(), e => e.Contains("发布程序目录"));
        Assert.DoesNotContain(new AiServiceSettings { DataHome = Path.Combine(Path.GetTempPath(), "VisionStudio-Independent-AI") }.Validate(), e => e.Contains("发布程序目录"));
    }
    public sealed record HiddenParameters
    { public double Threshold { get; init; } = .5; [Browsable(false)] public string SecretPin { get; init; } = ""; }
}

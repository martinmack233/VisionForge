using System.Text.Json;
using VisionPlatform.Contracts.Images;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Editor.Core.Persistence;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Imaging;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class Phase4AWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VisionStudio-Workspace-" + Guid.NewGuid().ToString("N"));
    private WorkspaceSettings Settings => new() { DefaultProjectDirectory = Path.Combine(_root, "projects"), ExamplesDirectory = Path.Combine(_root, "examples"), LogDirectory = Path.Combine(_root, "logs"), LogEnabled = false };
    private async Task Configure(WorkspaceViewModel workspace)
    {
        await workspace.InitializeAsync(); workspace.DefaultProjectDirectory = Settings.DefaultProjectDirectory; workspace.ExamplesDirectory = Settings.ExamplesDirectory;
        workspace.LogDirectory = Settings.LogDirectory; workspace.LogEnabled = false; await workspace.SaveSettingsAsync();
    }

    [Fact]
    public async Task VersionedWorkspaceSettingsAndRecentListRoundTripWithoutTouchingUnrelatedJson()
    {
        Directory.CreateDirectory(_root); var unrelated = Path.Combine(_root, "user.json"); await File.WriteAllTextAsync(unrelated, "user-data");
        var store = new WorkspaceStateStore(_root);
        var projects = Enumerable.Range(0, 30).Select(i => new RecentProjectEntry(Path.Combine(_root, i + ".json"), "P" + i, DateTimeOffset.UtcNow)).ToArray();
        await store.SaveAsync(new(1, Settings with { ResultImageBudgetMiB = 1024 }, projects));
        var loaded = await store.LoadAsync(); Assert.Equal(1, loaded.SchemaVersion); Assert.Equal(1024, loaded.Settings.ResultImageBudgetMiB);
        Assert.Equal(20, loaded.RecentProjects.Count); Assert.Equal("user-data", await File.ReadAllTextAsync(unrelated));
        var original = await File.ReadAllBytesAsync(store.FilePath);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new(1, Settings with { ResultImageBudgetMiB = 0 }, [])));
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath));
    }

    [Fact]
    public async Task UnsupportedWorkspaceVersionFailsWithoutReplacingUserState()
    {
        Directory.CreateDirectory(_root); var store = new WorkspaceStateStore(_root);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new WorkspaceState(99, Settings, [])));
        var before = await File.ReadAllTextAsync(store.FilePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Equal(before, await File.ReadAllTextAsync(store.FilePath));
    }

    [Fact]
    public async Task NavigationKeepsTheSameLiveImageNodeAndRevision()
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, "input.png");
        using (var builder = new ImageFrameBuilder(16, 12, PixelFormat.Mono8))
        { builder.GetRowSpan(5).Fill(123); using var image = builder.Build(); ImageFile.Save(image, path); }
        using var session = new EditorSession(); await session.ImportImageAsync(path);
        using var workspace = new WorkspaceViewModel(session, Path.Combine(_root, "state")); await Configure(workspace);
        var imageRef = session.DisplayImage; var node = session.SelectedNode; var revision = session.Revision;
        foreach (var page in Enum.GetValues<WorkspacePage>()) workspace.CurrentPage = page;
        workspace.NavigationExpanded = false; Assert.Equal(64, workspace.NavigationWidth);
        Assert.Same(imageRef, session.DisplayImage); Assert.Same(node, session.SelectedNode); Assert.Equal(revision, session.Revision);
        Assert.False(imageRef!.IsDisposed); using var lease = imageRef.Lease(); Assert.Equal(123, lease.GetRowSpan(5)[4]);
    }

    [Fact]
    public async Task ProjectCopyAndRenameKeepValidResourcesAndPreserveSource()
    {
        Directory.CreateDirectory(_root); var imagePath = Path.Combine(_root, "source.png");
        using (var builder = new ImageFrameBuilder(8, 8, PixelFormat.Mono8)) { using var image = builder.Build(); ImageFile.Save(image, imagePath); }
        var path = Path.Combine(_root, "source.json"); var store = new ProjectStore(BuiltinRegistration.CreateRegistry());
        var original = new ProjectDocument { ProjectName = "原工程", Nodes = [new("input", "ImageInput", "输入", JsonDefaults.Element(new ImageInputParameters { Path = "source.png" }))] };
        await store.SaveAsync(path, original); await EditorLayoutStore.SaveAsync(path, new(1, original.ProjectId, [new("input", 7.5, 12.5)])); var bytes = await File.ReadAllBytesAsync(path);
        using var session = new EditorSession(); using var workspace = new WorkspaceViewModel(session, Path.Combine(_root, "state")); await Configure(workspace);
        await workspace.RememberProjectAsync(path, original.ProjectName); workspace.SelectedProject = Assert.Single(workspace.RecentProjects); workspace.CopyName = "独立副本";
        await workspace.CopySelectedProjectAsync(); var target = workspace.SelectedProject!.Path;
        Assert.NotEqual(path, target); var copied = await store.LoadAsync(target); Assert.NotEqual(original.ProjectId, copied.ProjectId);
        var copiedLayout = await EditorLayoutStore.LoadAsync(target, copied.ProjectId); Assert.NotNull(copiedLayout); Assert.Equal(7.5, Assert.Single(copiedLayout!.Nodes).X);
        var input = copied.Nodes.Single().Parameters.Deserialize<ImageInputParameters>(JsonDefaults.Options)!; Assert.Equal(imagePath, input.Path);
        workspace.RenameText = "副本重命名"; await workspace.RenameSelectedProjectAsync();
        Assert.Equal("副本重命名", (await store.LoadAsync(target)).ProjectName); Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        await workspace.OpenProjectAsync(target); await session.RunAsync(); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
        workspace.ProjectSearch = "副本重命名"; Assert.Single(workspace.RecentProjects); workspace.ProjectSearch = "不存在"; Assert.Empty(workspace.RecentProjects);
    }

    [Fact]
    public async Task BundledTemplatesCannotBeRenamedOrOpenedThroughAnUnprotectedFallback()
    {
        Directory.CreateDirectory(Settings.ExamplesDirectory); var path = Path.Combine(Settings.ExamplesDirectory, "project.json");
        await new ProjectStore(BuiltinRegistration.CreateRegistry()).SaveAsync(path, new() { Nodes = [new("logic", "LogicNot", "逻辑", JsonDefaults.Element(new { }))] }); var before = await File.ReadAllBytesAsync(path);
        using var session = new EditorSession(); using var workspace = new WorkspaceViewModel(session, Path.Combine(_root, "state")); await Configure(workspace);
        await workspace.RememberProjectAsync(path, "模板"); workspace.SelectedProject = workspace.RecentProjects.Single(); workspace.RenameText = "禁止写回";
        await Assert.ThrowsAsync<InvalidOperationException>(workspace.RenameSelectedProjectAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.OpenProjectAsync(path));
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Null(session.ProjectPath);
    }

    [Fact]
    public async Task MonitorReadsTheCurrentRuntimeOutputsAndRecordsRealRuns()
    {
        using var session = new EditorSession(); session.AddNode("LogicNot"); var logic = session.SelectedNode!; session.AddNode("ResultDisplay"); var report = session.SelectedNode!;
        Assert.True(session.TryConnect(logic.Outputs.Single(p => p.Name == "Result"), report.Inputs.Single(p => p.Name == "IsOk"), out _));
        using var workspace = new WorkspaceViewModel(session, Path.Combine(_root, "state")); await Configure(workspace);
        session.SelectedNode = logic; await session.RunAsync();
        Assert.Equal("OK", workspace.CurrentDecision); var run = Assert.Single(workspace.RunHistory);
        Assert.Equal("Success", run.Status); Assert.Equal(2, run.NodeCount); Assert.True(run.Milliseconds >= 0); Assert.Single(workspace.Logs);
        await session.NewAsync(); Assert.Equal("尚无判定输出", workspace.CurrentDecision); Assert.Single(workspace.RunHistory);
    }

    public void Dispose()
    {
        var root = Path.GetFullPath(_root); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("VisionStudio-Workspace-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test fixture.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

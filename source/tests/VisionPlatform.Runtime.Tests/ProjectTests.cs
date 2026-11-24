using System.Security.Cryptography;
using System.Text.Json;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Runtime.Tests;

public sealed class ProjectTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VisionPlatform.RuntimeTests-" + Guid.NewGuid().ToString("N"));
    private string ProjectPath => Path.Combine(_directory, "project.json");
    public ProjectTests() => Directory.CreateDirectory(_directory);
    private static NodeRegistry Registry()
    {
        var registry = new NodeRegistry();
        registry.Register("discovered", () => new DiscoverableNode());
        return registry;
    }
    private static ProjectDocument Project() => new()
    {
        ProjectName = "Recipe-A",
        Nodes = [RuntimeTests.Spec("source", "discovered", new { KernelSize = 5, SigmaX = 1.2 })],
        ROI = [new(12, 15, 7, 9, 20)]
    };

    [Fact]
    public async Task SaveLoadPreservesGraphParametersRoiResourcesAndExecution()
    {
        var resource = Path.Combine(_directory, "template.bin");
        await File.WriteAllBytesAsync(resource, [1, 2, 3, 4]);
        var original = Project() with { TemplateResources = [new("template", "template.bin", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(resource))))], AlgorithmVersion = new() { ["test.matcher"] = "1.0.0" } };
        var registry = Registry();
        var store = new ProjectStore(registry, new Dictionary<string, string> { ["test.matcher"] = "1.0.0" });
        await store.SaveAsync(ProjectPath, original);
        var loaded = await store.LoadAsync(ProjectPath);
        Assert.Equal(original.ProjectId, loaded.ProjectId);
        Assert.Equal(original.Nodes[0].Parameters.GetProperty("KernelSize").GetInt32(), loaded.Nodes[0].Parameters.GetProperty("KernelSize").GetInt32());
        Assert.Equal(original.Nodes[0].Parameters.GetProperty("SigmaX").GetDouble(), loaded.Nodes[0].Parameters.GetProperty("SigmaX").GetDouble());
        Assert.Equal(original.Nodes[0].Parameters.EnumerateObject().Select(p => p.Name), loaded.Nodes[0].Parameters.EnumerateObject().Select(p => p.Name));
        Assert.Equal(original.ROI[0], loaded.ROI[0]);
        Assert.Equal(original.TemplateResources[0], loaded.TemplateResources[0]);
        using var before = await new RuntimeExecutor(registry).ExecuteAsync(original.ToGraph(), _directory);
        using var after = await new RuntimeExecutor(registry).ExecuteAsync(loaded.ToGraph(), _directory);
        Assert.Equal(before.Status, after.Status);
        Assert.True(after.Success);
    }

    [Fact]
    public async Task ReplaceSavesOnlyTargetAndPreservesUnrelatedJson()
    {
        var store = new ProjectStore(Registry());
        var unrelated = Path.Combine(_directory, "customer-data.json");
        await File.WriteAllTextAsync(unrelated, "keep me");
        var original = Project();
        await store.SaveAsync(ProjectPath, original);
        await store.SaveAsync(ProjectPath, original with { ProjectName = "Recipe-B" });
        Assert.Equal("Recipe-B", (await store.LoadAsync(ProjectPath)).ProjectName);
        Assert.Equal("keep me", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task FailedValidationPreservesExistingProjectBytes()
    {
        var store = new ProjectStore(Registry());
        var original = Project();
        await store.SaveAsync(ProjectPath, original);
        var bytes = await File.ReadAllBytesAsync(ProjectPath);
        var error = await Assert.ThrowsAsync<ProjectValidationException>(() => store.SaveAsync(ProjectPath, original with { SchemaVersion = 99 }));
        Assert.Contains("SchemaVersion", error.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ProjectPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CancelledSavePreservesExistingProject()
    {
        var store = new ProjectStore(Registry());
        var original = Project();
        await store.SaveAsync(ProjectPath, original);
        var bytes = await File.ReadAllBytesAsync(ProjectPath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(ProjectPath, original with { ProjectName = "changed" }, cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ProjectPath));
    }

    [Fact]
    public async Task FailedReplacementCleansOnlyOwnedTemporaryFile()
    {
        var store = new ProjectStore(Registry());
        var occupied = Path.Combine(_directory, "occupied.json");
        Directory.CreateDirectory(occupied);
        var marker = Path.Combine(occupied, "marker.txt");
        await File.WriteAllTextAsync(marker, "original");
        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(occupied, Project()));
        Assert.Equal("original", await File.ReadAllTextAsync(marker));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task MissingTemplateIsExplicitAndBlocksLoad()
    {
        var store = new ProjectStore(Registry());
        var project = Project() with { TemplateResources = [new("fiducial", "missing.png")] };
        await File.WriteAllTextAsync(ProjectPath, JsonSerializer.Serialize(project, JsonDefaults.Options));
        var error = await Assert.ThrowsAsync<ProjectValidationException>(() => store.LoadAsync(ProjectPath));
        Assert.Contains("fiducial", error.Message);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void AlgorithmAndPluginVersionsAreValidated()
    {
        var project = Project() with { AlgorithmVersion = new() { ["matcher"] = "2" } };
        Assert.Contains(new ProjectStore(Registry()).Validate(project, _directory), e => e.Contains("not installed"));
        Assert.Contains(new ProjectStore(Registry(), new Dictionary<string, string> { ["matcher"] = "1" }).Validate(project, _directory), e => e.Contains("requires version"));
        var plugin = Project() with { Nodes = [RuntimeTests.Spec("a", "discovered") with { PluginVersion = "2" }] };
        Assert.Contains(new ProjectStore(Registry()).Validate(plugin, _directory), e => e.Contains("plugin version"));
    }

    [Fact]
    public async Task CorruptOrFutureProjectDoesNotFallBackToDefaultParameters()
    {
        var store = new ProjectStore(Registry());
        await File.WriteAllTextAsync(ProjectPath, "{broken");
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(ProjectPath));
        var future = Project() with { SchemaVersion = ProjectDocument.CurrentSchemaVersion + 1 };
        await File.WriteAllTextAsync(ProjectPath, JsonSerializer.Serialize(future, JsonDefaults.Options));
        await Assert.ThrowsAsync<ProjectValidationException>(() => store.LoadAsync(ProjectPath));
        await File.WriteAllTextAsync(ProjectPath, "{\"UnexpectedField\":true}");
        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(ProjectPath));
    }

    [Fact]
    public void ResourceHashChangeAndInvalidRoiAreRejected()
    {
        var path = Path.Combine(_directory, "template.bin"); File.WriteAllBytes(path, [1]);
        var project = Project() with { TemplateResources = [new("template", "template.bin", new string('0', 64))], ROI = [new(0, 0, -1, 10)] };
        var errors = new ProjectStore(Registry()).Validate(project, _directory);
        Assert.Contains(errors, e => e.Contains("SHA-256"));
        Assert.Contains(errors, e => e.Contains("ROI"));
    }

    [Fact]
    public async Task NullCollectionsAndUnknownPluginAreRejected()
    {
        var store = new ProjectStore(Registry());
        await File.WriteAllTextAsync(ProjectPath, JsonSerializer.Serialize(Project() with { Nodes = null! }, JsonDefaults.Options));
        await Assert.ThrowsAsync<ProjectValidationException>(() => store.LoadAsync(ProjectPath));
        var project = Project() with { Nodes = [RuntimeTests.Spec("missing", "unknown.plugin")] };
        Assert.Contains(store.Validate(project, _directory), e => e.Contains("not installed"));
    }

    public void Dispose()
    {
        var absolute = Path.GetFullPath(_directory);
        var allowed = Path.GetFullPath(Path.GetTempPath());
        if (!absolute.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("VisionPlatform.RuntimeTests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(absolute, recursive: true);
    }
}

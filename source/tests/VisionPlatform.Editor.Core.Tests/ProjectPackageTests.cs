using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisionPlatform.Algorithms.AI;
using VisionPlatform.Contracts.Deployment;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Deployment;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;
public sealed class ProjectPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vision-package-test-" + Guid.NewGuid().ToString("N"));
    private readonly VisionPlatform.Runtime.Graph.NodeRegistry _registry = BuiltinRegistration.CreateRegistry();
    private readonly Dictionary<string, string> _versions = new() { ["cvinspect.pattern"] = "0.34.0+0eddfbaa" };
    public ProjectPackageTests() => Directory.CreateDirectory(_root);
    private string At(string name) => Path.Combine(_root, name);
    private ProjectPackageStore Packages => new(_registry, _versions);
    private async Task<string> Sample(string name = "collection-multi-ocr") { await CollectionExampleFactory.SaveAllAsync(At("samples")); return At("samples/" + name + "/project.json"); }
    [Theory] [InlineData("collection-multi-ocr")] [InlineData("collection-blob-measure")]
    public async Task ImportedProjectMovesAndRunsThroughExistingEditorRuntime(string sample)
    {
        var original = await Sample(sample); var originalHash = ProjectPackageStore.HashFile(original); var file = At("example.vspkg");
        var plan = await Packages.ExportAsync(original, file); Assert.All(plan.Resources, r => Assert.True(r.Included));
        var inspect = await Packages.InspectAsync(file); Assert.True(inspect.CanImport, string.Join(";", inspect.Problems));
        var imported = await Packages.ImportAsync(file, new(At("different/location"), "导入副本"));
        using var session = new EditorSession(); await session.OpenAsync(imported); await session.RunAsync(); Assert.NotEmpty(session.CurrentOutputs); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
        Assert.Equal(originalHash, ProjectPackageStore.HashFile(original)); Assert.Equal("导入副本", session.ProjectName);
        Assert.Contains("Body", File.ReadAllText(imported)); Assert.DoesNotContain(_root.Replace("\\", "\\\\") + "\\\\samples", File.ReadAllText(imported));
        await Assert.ThrowsAsync<IOException>(() => Packages.ImportAsync(file, new(At("different/location")))); Assert.True(File.Exists(imported));
    }
    [Fact] public async Task TemplatesAndTheirImageAssetsArePortable()
    {
        var folder = At("template-source"); Directory.CreateDirectory(folder); DemoProjectFactory.CreateResources(folder);
        var source = Path.Combine(folder, "project.json"); await new ProjectStore(_registry, _versions).SaveAsync(source, DemoProjectFactory.Create(false));
        await Packages.ExportAsync(source, At("template.vspkg")); var imported = await Packages.ImportAsync(At("template.vspkg"), new(At("template-import")));
        using var session = new EditorSession(); await session.OpenAsync(imported); await session.RunAsync(); Assert.All(session.Results, r => Assert.Equal("Success", r.Status));
    }
    private async Task<(string Project, string Model)> ModelProject()
    {
        var source = await Sample("collection-blob-measure"); var model = At("source-model/model.onnx"); Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        // Hash/transport fixture only. Actual engine acceptance uses a valid generated ONNX separately.
        await File.WriteAllTextAsync(model, "package transport fixture, not an inference model");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(model)!, "deployment.json"), JsonSerializer.Serialize(new { schema_version = 1, task = "detect", model_file = "model.onnx", onnx_sha256 = ProjectPackageStore.HashFile(model) }));
        var store = new ProjectStore(_registry, _versions); var project = await store.LoadAsync(source);
        var yolo = DemoProjectFactory.Node("yolo", "YoloInference", new YoloInferenceOptions { ModelPath = model, PythonExecutable = "C:\\private-python\\python.exe", YoloStudioRoot = "C:\\private-source", ExpectedModelSha256 = ProjectPackageStore.HashFile(model) });
        project = project with { Nodes = [project.Nodes[0], yolo], Connections = [new("input", "Image", "yolo", "Image")] };
        await store.SaveAsync(source, project); return (source, model);
    }
    [Fact] public async Task ModelsAreExternalByDefaultAndRequireHashCheckedRelocation()
    {
        var (source, model) = await ModelProject(); var file = At("model.vspkg"); var manifest = await Packages.ExportAsync(source, file);
        var resource = Assert.Single(manifest.Resources, r => r.Kind == "Model"); Assert.False(resource.Included);
        using (var zip = ZipFile.OpenRead(file)) { Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".onnx")); using var reader = new StreamReader(zip.GetEntry("project.json")!.Open()); var json = await reader.ReadToEndAsync(); Assert.DoesNotContain("private-source", json); Assert.DoesNotContain("private-python", json); }
        await Assert.ThrowsAsync<InvalidDataException>(() => Packages.ImportAsync(file, new(At("missing-model")))); Assert.False(Directory.Exists(At("missing-model")));
        var relocated = await Packages.ImportAsync(file, new(At("external-import"), Relocations: new Dictionary<string, string> { [resource.Id] = model }));
        var restored = await new ProjectStore(_registry, _versions).LoadAsync(relocated); Assert.Equal(Path.GetFullPath(model), restored.Nodes.Single(n => n.NodeId == "yolo").Parameters.GetProperty("ModelPath").GetString());
        await File.AppendAllTextAsync(model, "changed"); var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Packages.ImportAsync(file, new(At("changed-model"), Relocations: new Dictionary<string, string> { [resource.Id] = model })));
        Assert.Contains("哈希变化", failure.Message); Assert.False(Directory.Exists(At("changed-model")));
    }
    [Fact] public async Task CarryingModelsRequiresExplicitRightsAndPinsBothFiles()
    {
        var (source, _) = await ModelProject(); await Assert.ThrowsAsync<ArgumentException>(() => Packages.ExportAsync(source, At("no-rights.vspkg"), new(true, false))); Assert.False(File.Exists(At("no-rights.vspkg")));
        var manifest = await Packages.ExportAsync(source, At("carried.vspkg"), new(true, true)); Assert.All(manifest.Resources, r => Assert.True(r.Included));
        var imported = await Packages.ImportAsync(At("carried.vspkg"), new(At("carried"))); var project = await new ProjectStore(_registry, _versions).LoadAsync(imported);
        var parameters = project.Nodes.Single(n => n.NodeId == "yolo").Parameters; Assert.Equal("", parameters.GetProperty("PythonExecutable").GetString()); Assert.StartsWith("resources/", parameters.GetProperty("ModelPath").GetString()); Assert.NotEmpty(parameters.GetProperty("ExpectedDeploymentSha256").GetString()!);
    }
    [Theory] [InlineData("../escape.txt")] [InlineData("C:/escape.txt")] [InlineData("\\\\server\\escape")] [InlineData("resources/CON.txt")] [InlineData("resources/name:stream")]
    public async Task UnsafeArchivePathsAreRejectedBeforeWriting(string path)
    {
        var file = At("bad.vspkg"); using (var z = ZipFile.Open(file, ZipArchiveMode.Create)) { z.CreateEntry("manifest.json"); z.CreateEntry(path); }
        var inspection = await Packages.InspectAsync(file); Assert.False(inspection.CanImport); await Assert.ThrowsAnyAsync<Exception>(() => Packages.ImportAsync(file, new(At("bad-import")))); Assert.False(Directory.Exists(At("bad-import")));
    }
    [Fact] public async Task CorruptionUnknownFieldsMissingFilesAndVersionMismatchAreRejected()
    {
        var source = await Sample(); var original = At("original.vspkg"); await Packages.ExportAsync(source, original);
        foreach (var mode in new[] { "corrupt", "unknown", "version", "missing", "duplicate", "link", "budget" })
        {
            var file = At(mode + ".vspkg"); File.Copy(original, file);
            using (var z = ZipFile.Open(file, ZipArchiveMode.Update))
            {
                if (mode == "corrupt") { var e = z.Entries.First(e => e.FullName.EndsWith(".png")); var n = e.FullName; e.Delete(); using var s = new StreamWriter(z.CreateEntry(n).Open()); s.Write("corrupt"); }
                else if (mode == "missing") z.Entries.First(e => e.FullName.EndsWith(".png")).Delete();
                else if (mode == "duplicate") z.CreateEntry("PROJECT.JSON");
                else if (mode == "link") z.CreateEntry("link").ExternalAttributes = 0xA1FF << 16;
                else if (mode == "budget") for (var i = 0; i <= ProjectPackageStore.MaximumEntries; i++) z.CreateEntry("extra/" + i);
                else { var e = z.GetEntry("manifest.json")!; JsonNode node; using (var reader = new StreamReader(e.Open())) node = JsonNode.Parse(reader.ReadToEnd())!; e.Delete(); if (mode == "unknown") node["Unknown"] = true; else node["PackageVersion"] = 99; using var writer = new StreamWriter(z.CreateEntry("manifest.json").Open()); writer.Write(node.ToJsonString()); }
            }
            Assert.False((await Packages.InspectAsync(file)).CanImport); Assert.False(Directory.Exists(At(mode + "-import")));
        }
        Assert.Empty(Directory.EnumerateDirectories(_root, ".vision-import-*"));
    }
    [Fact] public async Task CancellationDoesNotLeaveSuccessfulPackageOrProject()
    {
        var source = await Sample(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Packages.ExportAsync(source, At("cancel.vspkg"), ct: cts.Token)); Assert.False(File.Exists(At("cancel.vspkg")));
        await Packages.ExportAsync(source, At("valid.vspkg")); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Packages.ImportAsync(At("valid.vspkg"), new(At("cancel-import")), cts.Token)); Assert.False(Directory.Exists(At("cancel-import")));
    }
    [Fact] public async Task SetupIsOptionalAndExportsNoBusinessPathsOrModelHashes()
    {
        var config = new MachineConfiguration { DataDirectory = At("private-data"), CacheDirectory = At("private-cache"), LogDirectory = At("private-log") };
        var report = await SetupDiagnostics.CheckAsync(config, At("secret-business-model.onnx"));
        Assert.Equal(SetupState.Available, report.Checks.Single(c => c.Id == "core").State); Assert.Equal(SetupState.Unconfigured, report.Checks.Single(c => c.Id == "ai-runtime").State); Assert.Equal(SetupState.Missing, report.Checks.Single(c => c.Id == "model").State);
        var export = At("diagnostic.json"); await SetupDiagnostics.ExportRedactedAsync(export, report); var text = await File.ReadAllTextAsync(export); Assert.DoesNotContain("private-data", text); Assert.DoesNotContain("secret-business", text); Assert.DoesNotContain(_root, text);
        var store = new MachineConfigurationStore(At("machine.json")); await store.SaveAsync(config); await store.SaveAsync(config with { SetupCompleted = true }); Assert.True(store.Load().SetupCompleted); Assert.True(File.Exists(store.FilePath + ".previous"));
    }
    [Fact] public async Task IncompatiblePluginAndAlgorithmDoNotCreateImportDirectory()
    {
        var source = await Sample(); var store = new ProjectStore(_registry, _versions); var project = await store.LoadAsync(source);
        // A valid package from another installation may require an algorithm absent here.
        var extra = new Dictionary<string, string>(_versions) { ["external.algorithm"] = "9.9" };
        await new ProjectStore(_registry, extra).SaveAsync(source, project with { AlgorithmVersion = extra });
        await new ProjectPackageStore(_registry, extra).ExportAsync(source, At("dependencies.vspkg"));
        var inspection = await Packages.InspectAsync(At("dependencies.vspkg")); Assert.False(inspection.CanImport); Assert.Contains(inspection.Problems, p => p.Contains("external.algorithm"));
        await Assert.ThrowsAsync<ProjectValidationException>(() => Packages.ImportAsync(At("dependencies.vspkg"), new(At("incompatible")))); Assert.False(Directory.Exists(At("incompatible")));
    }
    [Fact] public async Task LegacySchemaMigrationKeepsRecoverableOriginal()
    {
        var source = await Sample(); var json = JsonNode.Parse(await File.ReadAllTextAsync(source))!; json["SchemaVersion"] = 1; json["AiProject"] = null;
        await File.WriteAllTextAsync(source, json.ToJsonString()); var original = await File.ReadAllBytesAsync(source);
        var store = new ProjectStore(_registry, _versions); var loaded = await store.LoadAsync(source); await store.SaveAsync(source, loaded);
        var backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(source)!, "project.json.schema1.*.bak")); Assert.Equal(original, await File.ReadAllBytesAsync(backup)); Assert.Equal(3, JsonNode.Parse(await File.ReadAllTextAsync(source))!["SchemaVersion"]!.GetValue<int>());
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

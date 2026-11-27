using VisionPlatform.Contracts.Collections;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Editor.Core.Workspace;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class FilterGeometryExamplesTests
{
    [Fact]
    public async Task ExamplesRunAfterSavePackageAndRelocationWithStableSourceAssociations()
    {
        var scratch = Directory.CreateTempSubdirectory("VisionForge-filter-geometry-");
        try
        {
            var registry = BuiltinRegistration.CreateRegistry();
            var store = new ProjectStore(registry);
            var catalog = new ExampleCatalog(WorkspaceSettings.FindExamples(), store);
            var examples = await catalog.LoadAsync();
            var packages = new ProjectPackageStore(registry);
            foreach (var id in new[] { "image-filter-methods", "contour-envelope-geometry" })
            {
                var entry = examples.Single(e => e.Manifest.Id == id);
                var copy = await catalog.CopyAsync(entry, scratch.FullName);
                var source = await store.LoadAsync(copy);
                await store.SaveAsync(copy, source);
                var archive = Path.Combine(scratch.FullName, id + ".vspkg");
                await packages.ExportAsync(copy, archive, new());
                var relocation = await packages.ImportAsync(archive, new(Path.Combine(scratch.FullName, "relocated-" + id)));
                var reopened = await store.LoadAsync(relocation);
                Assert.Equal(source.Nodes.Where(n => n.NodeId != "input").Select(n => n.Parameters.GetRawText()),
                    reopened.Nodes.Where(n => n.NodeId != "input").Select(n => n.Parameters.GetRawText()));
                var relocatedImage = reopened.Nodes.Single(n => n.NodeId == "input").Parameters.GetProperty("Path").GetString()!;
                Assert.NotEqual("input.png", relocatedImage);
                Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(relocation)!, relocatedImage)));
                using var run = await new RuntimeExecutor(registry).ExecuteAsync(reopened.ToGraph(), Path.GetDirectoryName(relocation)!);
                var verification = ExampleVerifier.Verify(entry.Manifest, run);
                Assert.True(verification.Passed, verification.Summary);
                if (id == "contour-envelope-geometry")
                {
                    var sorted = run.Nodes["sort"].Output<VisionCollection>("Collection");
                    var each = run.Nodes["each"].Output<VisionCollection>("Results");
                    Assert.Equal(sorted.Items.Select(i => i.Origin), each.Items.Select(i => i.Origin));
                    Assert.Equal(sorted.Items.Select(i => i.Fields["Diameter"].Number),
                        each.Items.Cast<IterationItem>().Select(i => i.Result!.Number));
                    Assert.Same(sorted.Items[0], run.Nodes["select"].Output<VisionItem>("Item"));
                    Assert.Equal(7, run.Nodes["axis"].Output<VisionCollection>("Rectangles").Count);
                    Assert.Equal(0, run.Nodes["rotated"].Output<VisionCollection>("Circles").Count);
                }
                Assert.Empty(await catalog.ValidateAsync(entry));
            }

            var old = examples.Single(e => e.Manifest.Id == "inspection-color-shapes");
            using var existing = await new RuntimeExecutor(registry).ExecuteAsync((await store.LoadAsync(old.ProjectPath)).ToGraph(), old.Directory);
            Assert.True(existing.Success, existing.ErrorMessage);
            Assert.Equal(2, existing.Nodes["shape"].Output<VisionCollection>("Rectangles").Count);
        }
        finally { scratch.Delete(recursive: true); }
    }
}

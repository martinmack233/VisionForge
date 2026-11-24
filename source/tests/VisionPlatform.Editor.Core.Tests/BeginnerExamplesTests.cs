using System.Text.Json;
using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Geometry;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Examples;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Plugins.Samples;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Projects;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class BeginnerExamplesTests
{
    // All test artifacts stay in an independent managed test directory, never a user's project.
    [Fact]
    public async Task LessonsCopyRunAndReopenThroughTheExistingRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionForge-Beginner-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        await BeginnerExampleFactory.SaveAllAsync(source);
        var registry = BuiltinRegistration.CreateRegistry(); var store = new ProjectStore(registry);
        var catalog = new ExampleCatalog(source, store); var entries = await catalog.LoadAsync();
        Assert.Empty(catalog.LoadErrors); Assert.Equal(5, entries.Count);
        foreach (var entry in entries)
        {
            Assert.Equal(BeginnerExampleFactory.Category, entry.Manifest.Category);
            Assert.NotEmpty(entry.Manifest.LearningGoals); Assert.NotEmpty(entry.Manifest.TryIt);
            Assert.Empty(await catalog.ValidateAsync(entry));
            var path = await catalog.CopyAsync(entry, Path.Combine(root, "copies"));
            var project = await store.LoadAsync(path);
            using var run = await new RuntimeExecutor(registry).ExecuteAsync(project.ToGraph(), Path.GetDirectoryName(path)!);
            Assert.True(run.Success, run.ErrorMessage);
            var check = ExampleVerifier.Verify(entry.Manifest, run); Assert.True(check.Passed, check.Summary);
            await store.SaveAsync(path, project);
            Assert.Equal(project.Nodes.Count, (await store.LoadAsync(path)).Nodes.Count);
            Assert.Empty(await catalog.ValidateAsync(entry)); // executing/editing copies does not modify built-ins
            if (entry.Manifest.Id == BeginnerExampleFactory.Ids[4])
            {
                var results = Assert.IsType<VisionCollection>(run.Nodes["each"].Outputs["Results"]);
                var items = results.Items.Cast<IterationItem>().ToArray();
                Assert.Equal(new double?[] { 1, 2, 3 }, items.Select(i => i.Result!.Number));
                Assert.Equal(new[] { BusinessState.Ng, BusinessState.Ok, BusinessState.Ok }, items.Select(i => i.State));
                Assert.Equal(new[] { "区域索引=0", "区域索引=1", "区域索引=2" }, items.Select(i => i.Result!.Text));
                Assert.Equal(3, items.Select(i => i.IterationRunId).Distinct().Count());
            }
            if (entry.Manifest.Id == BeginnerExampleFactory.Ids[1])
            {
                var transform = Assert.IsType<CoordinateTransform2D>(run.Nodes["crop"].Outputs["CoordinateTransform"]);
                Assert.Equal(new Point2D(30, 30), transform.Map(new(0, 0)));
            }
        }
    }

    [Fact]
    public async Task GuidedLoopExerciseChangesBusinessResultsWithoutChangingExecutionSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "VisionForge-Beginner-Exercise-" + Guid.NewGuid().ToString("N"));
        await BeginnerExampleFactory.SaveAllAsync(root);
        var project = BeginnerExampleFactory.Create(4);
        var each = project.Nodes.Single(n => n.NodeId == "each");
        var options = each.Parameters.Deserialize<ForEachOptions>(JsonDefaults.Options)!;
        var judge = options.Body.Nodes.Single(n => n.NodeId == "judge");
        options.Body.Nodes[options.Body.Nodes.IndexOf(judge)] = judge with { Parameters = JsonDefaults.Element(new CompareOptions { Operator = CompareOperator.GreaterOrEqual, ValueB = 3 }) };
        project.Nodes[project.Nodes.IndexOf(each)] = each with { Parameters = JsonDefaults.Element(options) };
        using var run = await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteAsync(project.ToGraph(), Path.Combine(root, BeginnerExampleFactory.Ids[4]));
        Assert.True(run.Success, run.ErrorMessage);
        var results = Assert.IsType<VisionCollection>(run.Nodes["each"].Outputs["Results"]);
        Assert.Equal(new[] { BusinessState.Ng, BusinessState.Ng, BusinessState.Ok }, results.Items.Select(i => i.State));
    }
}

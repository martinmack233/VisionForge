using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Parameters;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class ParameterDefaultRegressionTests
{
    [Fact]
    public void RestoringNullablePluginDefaultsKeepsANonNullDefaultValue()
    {
        var definition = new NodeAuthoringDefinition("plugin", "插件", "测试", typeof(PluginOptions), JsonDefaults.Element(new PluginOptions()));
        var draft = new ParameterDraft(definition, JsonDefaults.Element(new PluginOptions { Level = null }));
        draft.ResetDefaults();
        Assert.True(draft.TryBuild(out var value, out var errors), string.Join("; ", errors));
        Assert.Equal(7, value.GetProperty("Level").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("OptionalLevel").ValueKind);
    }
    public sealed record PluginOptions { public int? Level { get; init; } = 7; public int? OptionalLevel { get; init; } }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Plugins.Builtin;
using Xunit;

namespace VisionPlatform.Editor.Core.Tests;

public sealed class ParameterTests
{
    [Fact]
    public void AllBuiltinMetadataIsGeneratedFromParameterTypes()
    {
        var catalog=new NodeAuthoringCatalog();
        foreach(var definition in catalog.Items)
        {
            var metadata=ParameterMetadataService.Describe(definition);
            Assert.NotEmpty(metadata.Fields);
            var draft=new ParameterDraft(definition,definition.DefaultParameters);
            Assert.True(draft.TryBuild(out var data,out var errors),string.Join(";",errors));
            Assert.Equal(JsonValueKind.Object,data.ValueKind);
        }
    }
    [Fact]
    public void EditingNestedDraftDoesNotMutateOriginalSnapshot()
    {
        var definition=new NodeAuthoringCatalog().Get("GaussianBlur");
        var original=definition.DefaultParameters.GetRawText();
        var draft=new ParameterDraft(definition,definition.DefaultParameters);
        draft.Fields.Single(f=>f.Descriptor.Path=="Options.KernelSize").Text="9";
        Assert.True(draft.TryBuild(out var json,out _));
        Assert.Equal(9,json.GetProperty("Options").GetProperty("KernelSize").GetInt32());
        Assert.Equal(original,definition.DefaultParameters.GetRawText());
        Assert.Equal(5,definition.DefaultParameters.GetProperty("Options").GetProperty("KernelSize").GetInt32());
    }
    [Theory]
    [InlineData("Options.KernelSize","4.5")]
    [InlineData("Options.SigmaX","-1")]
    [InlineData("Options.SigmaY","NaN")]
    [InlineData("Options.BorderType","unknown")]
    public void InvalidTypedValuesAndRangesAreRejected(string path,string text)
    {
        var definition=new NodeAuthoringCatalog().Get("GaussianBlur");
        var draft=new ParameterDraft(definition,definition.DefaultParameters);
        draft.Fields.Single(f=>f.Descriptor.Path==path).Text=text;
        Assert.False(draft.TryBuild(out _,out var errors));
        Assert.Contains(errors,e=>e.StartsWith(path,StringComparison.Ordinal));
    }
    [Fact]
    public void OptionalNestedRoiCanBeEnabledAndClearedWithoutSchemaChanges()
    {
        var definition=new NodeAuthoringCatalog().Get("GaussianBlur");
        var draft=new ParameterDraft(definition,definition.DefaultParameters);
        var section=draft.Sections.Single(s=>s.Descriptor.Path=="ROI");
        Assert.False(section.Enabled);
        section.Enabled=true;
        draft.Fields.Single(f=>f.Descriptor.Path=="ROI.X").Text="12";
        draft.Fields.Single(f=>f.Descriptor.Path=="ROI.Width").Text="32";
        Assert.True(draft.TryBuild(out var enabled,out _));
        Assert.Equal(12,enabled.GetProperty("ROI").GetProperty("X").GetDouble());
        Assert.Equal(32,enabled.GetProperty("ROI").GetProperty("Width").GetDouble());
        section.Enabled=false;
        Assert.True(draft.TryBuild(out var disabled,out _));
        Assert.Equal(JsonValueKind.Null,disabled.GetProperty("ROI").ValueKind);
    }
    [Fact]
    public void NullableEnumsPreserveAutoFormatAndAllowExplicitBayer()
    {
        var definition=new NodeAuthoringCatalog().Get("ImageInput");
        var draft=new ParameterDraft(definition,definition.DefaultParameters);
        var format=draft.Fields.Single(f=>f.Descriptor.Path=="PixelFormat");
        Assert.True(format.Descriptor.Nullable);Assert.False(format.UseValue);
        Assert.True(draft.TryBuild(out var auto,out _));Assert.Equal(JsonValueKind.Null,auto.GetProperty("PixelFormat").ValueKind);
        format.UseValue=true;format.Text="BayerGB8";
        Assert.True(draft.TryBuild(out var explicitFormat,out _));Assert.Equal("BayerGB8",explicitFormat.GetProperty("PixelFormat").GetString());
    }
    [Fact]
    public void ScalarKindsAndDataAnnotationMetadataWorkForNewPlugins()
    {
        var definition=new NodeAuthoringDefinition("Example","Example","Tests",typeof(ExampleParameters),JsonDefaults.Element(new ExampleParameters()));
        var draft=new ParameterDraft(definition,definition.DefaultParameters);
        Assert.Equal(ParameterKind.Boolean,draft.Fields.Single(f=>f.Descriptor.Path=="Enabled").Descriptor.Kind);
        Assert.Equal(ParameterKind.String,draft.Fields.Single(f=>f.Descriptor.Path=="Name").Descriptor.Kind);
        var count=draft.Fields.Single(f=>f.Descriptor.Path=="Count");Assert.Equal("样本数",count.Descriptor.Label);Assert.Equal(1,count.Descriptor.Minimum);Assert.Equal(8,count.Descriptor.Maximum);
        draft.Fields.Single(f=>f.Descriptor.Path=="Name").Text="part-42";
        draft.Fields.Single(f=>f.Descriptor.Path=="Enabled").BooleanValue=true;
        Assert.True(draft.TryBuild(out var json,out _));Assert.True(json.GetProperty("Enabled").GetBoolean());Assert.Equal("part-42",json.GetProperty("Name").GetString());
        count.Text="9";Assert.False(draft.TryBuild(out _,out _));
    }
    [Fact]
    public void RestoreDefaultsResetsDraftOnly()
    {
        var definition=new NodeAuthoringCatalog().Get("GaussianBlur");
        var draft=new ParameterDraft(definition,JsonDefaults.Element(new GaussianBlurParameters {Options=new(){KernelSize=11,SigmaX=2}}));
        draft.ResetDefaults();Assert.True(draft.TryBuild(out var json,out _));Assert.Equal(5,json.GetProperty("Options").GetProperty("KernelSize").GetInt32());
    }
    public sealed record ExampleParameters
    {
        [Range(1,8),DisplayName("样本数")]public int Count {get;init;}=3;
        public double Level {get;init;}=.5;
        public bool Enabled {get;init;}
        public string Name {get;init;}="part";
    }
}

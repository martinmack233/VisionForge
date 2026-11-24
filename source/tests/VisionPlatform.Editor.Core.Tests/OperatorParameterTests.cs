using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Editor.Core.Catalog;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Plugins.Builtin;
using Xunit;
namespace VisionPlatform.Editor.Core.Tests;
public sealed class OperatorParameterTests
{
 [Fact]public void InactiveRandomFieldsPreserveFormalValuesAndDoNotBlockSingleMode()
 {var definition=new NodeAuthoringCatalog().Get("RandomNumber");var original=JsonDefaults.Element(new RandomNumberOptions{Mode=RandomOutputMode.Single,Count=7});var draft=new ParameterDraft(definition,original);var count=draft.Fields.Single(f=>f.Descriptor.Path=="Count");count.Text="invalid";Assert.False(draft.IsApplicable(count));Assert.True(draft.TryBuild(out var built,out _));Assert.Equal(7,built.GetProperty("Count").GetInt32());draft.Fields.Single(f=>f.Descriptor.Path=="Mode").Text="Collection";Assert.True(draft.IsApplicable(count));Assert.False(draft.TryBuild(out _,out var errors));Assert.Contains(errors,e=>e.StartsWith("Count:"));Assert.Equal(7,original.GetProperty("Count").GetInt32());}
 [Fact]public void TcpNestedApplicabilityFollowsRoleAndOperationWithoutSideEffects()
 {var d=new ParameterDraft(new NodeAuthoringCatalog().Get("TcpCommunication"),JsonDefaults.Element(new TcpCommunicationOptions()));var port=d.Fields.Single(f=>f.Descriptor.Path=="Client.Port");var listener=d.Fields.Single(f=>f.Descriptor.Path=="Listener.Port");Assert.True(d.IsApplicable(port));Assert.False(d.IsApplicable(listener));d.Fields.Single(f=>f.Descriptor.Path=="Role").Text="Server";Assert.False(d.IsApplicable(port));Assert.True(d.IsApplicable(listener));d.Fields.Single(f=>f.Descriptor.Path=="Operation").Text="Receive";Assert.False(d.IsApplicable(listener));}
}

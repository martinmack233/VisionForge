using VisionPlatform.Algorithms.Logic;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;
using VisionPlatform.Plugins.Builtin;
using VisionPlatform.Runtime.Execution;
using VisionPlatform.Runtime.Graph;
using Xunit;
namespace VisionPlatform.Algorithms.Tests;
public sealed class BooleanExpansionTests
{
    [Theory][InlineData(BooleanOperationKind.And,true,false,false)][InlineData(BooleanOperationKind.Or,true,false,true)][InlineData(BooleanOperationKind.Xor,true,true,false)][InlineData(BooleanOperationKind.Xor,true,false,true)]
    public async Task RealBooleanValues(BooleanOperationKind op,bool a,bool b,bool expected)
    {var node=new NodeSpecification("logic","BooleanOperation","布尔",JsonDefaults.Element(new BooleanOperationOptions{Operation=op}));using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(node,new Dictionary<string,object>{{"A",a},{"B",b}});Assert.True(run.Success);Assert.Equal(expected,run.Nodes["logic"].Output<bool>("Result"));}
    [Fact]public async Task NotNeedsOnlyAAndOtherOperationsRejectMissingOrStringValues()
    {
        var runtime=new RuntimeExecutor(BuiltinRegistration.CreateRegistry());var not=new NodeSpecification("logic","BooleanOperation","布尔",JsonDefaults.Element(new BooleanOperationOptions{Operation=BooleanOperationKind.Not}));using(var run=await runtime.ExecuteSingleAsync(not,new Dictionary<string,object>{{"A",false}})){Assert.True(run.Success);Assert.True(run.Nodes["logic"].Output<bool>("Result"));}
        var and=not with{Parameters=JsonDefaults.Element(new BooleanOperationOptions())};var missing=await Assert.ThrowsAsync<GraphValidationException>(()=>runtime.ExecuteSingleAsync(and,new Dictionary<string,object>{{"A",false}}));Assert.Contains("B",missing.Message);
        await Assert.ThrowsAsync<GraphValidationException>(()=>runtime.ExecuteSingleAsync(not,new Dictionary<string,object>{{"A","false"}}));
        var registry=BuiltinRegistration.CreateRegistry();var source=new NodeSpecification("a","LogicNot","已配置布尔源",JsonDefaults.Element(new {Value=false}));var graph=new NodeGraph([source,and],[new("a","Result","logic","A")]);Assert.Contains(new GraphValidator(registry).Validate(graph),e=>e.Contains("logic.B"));Assert.Empty(new GraphValidator(registry).Validate(new([source,not],[new("a","Result","logic","A")])));
    }
    [Fact]public async Task ScalarSelectionPreservesIdentityAndType()
    {
        var origin=new ItemOrigin(Guid.NewGuid(),"modbus",3,null,"none","same-association");var scalar=new ScalarItem(origin,(CollectionValue)(ushort)65535);var node=new NodeSpecification("value","ItemValue","值",JsonDefaults.Element(new ItemValueOptions()));using var run=await new RuntimeExecutor(BuiltinRegistration.CreateRegistry()).ExecuteSingleAsync(node,new Dictionary<string,object>{{"Item",scalar}});Assert.True(run.Success);Assert.Equal(65535,run.Nodes["value"].Output<double>("Number"));Assert.Equal("same-association",scalar.Origin.Key);
    }
}

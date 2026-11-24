using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Contracts.Nodes;

namespace VisionPlatform.Plugins.Builtin;
public enum BooleanOperationKind
{
    [Display(Name="AND（逻辑与）",Description="A和B均为True。普通数据流会先求值上游，不控制外部操作。")] And,
    [Display(Name="OR（逻辑或）",Description="A和B至少一个True。不是IF/ELSE或控制流短路。")] Or,
    [Display(Name="NOT（逻辑非）",Description="只需要A，不需要连接B。")] Not,
    [Display(Name="XOR（逻辑异或）",Description="A和B不同时为True。仅逻辑Boolean，不是图像按位操作。")] Xor
}
public sealed record BooleanOperationOptions
{
    [DisplayName("逻辑运算"),Description("NOT只连接A；AND、OR、XOR必须连接A和B。缺失输入报错，不默认为false；数值比较和控制流分支使用各自节点。")]
    public BooleanOperationKind Operation{get;init;}
}
[SubflowSafe]
public sealed class BooleanOperationNode:NodeBase<BooleanOperationOptions>,IParameterDependentInputs
{
    public override NodeDescriptor Descriptor=>Describe("BooleanOperation",[new("A",typeof(bool)),new("B",typeof(bool),false)],[new("Result",typeof(bool))]);
    protected override IReadOnlyList<string> Validate(BooleanOperationOptions p)=>Enum.IsDefined(p.Operation)?[]:["请选择有效的Boolean逻辑运算。"];
    public IReadOnlyList<string> RequiredInputs(JsonElement parameters)=>parameters.Deserialize<BooleanOperationOptions>(JsonDefaults.Options)?.Operation==BooleanOperationKind.Not?[]:["B"];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var p=c.Parameters<BooleanOperationOptions>();var a=c.Input<bool>("A");
        var result=p.Operation switch{BooleanOperationKind.Not=>!a,BooleanOperationKind.And=>a&c.Input<bool>("B"),BooleanOperationKind.Or=>a|c.Input<bool>("B"),BooleanOperationKind.Xor=>a^c.Input<bool>("B"),_=>throw new ArgumentException("逻辑类型无效。")};
        return ValueTask.FromResult(new NodeOutput(new Dictionary<string,object>{{"Result",result}}));
    }
}
public enum ItemValueField
{
    [Display(Name="数值")] Number,[Display(Name="布尔值")] Boolean,[Display(Name="文本")] Text,[Display(Name="面积")] Area,
    [Display(Name="位置X")] X,[Display(Name="位置Y")] Y,[Display(Name="宽度")] Width,[Display(Name="高度")] Height,
    [Display(Name="置信度")] Confidence,[Display(Name="类别编号")] ClassId,[Display(Name="类别名称")] Class,
    [Display(Name="原始序号（0基）")] Index,[Display(Name="半径")] Radius,[Display(Name="线段长度")] Length,
    [Display(Name="周长")] Perimeter,[Display(Name="闭合")] Closed,[Display(Name="判定有效")] Valid
}
public sealed record ItemValueOptions
{[DisplayName("读取项目字段"),Description("仅读取当前项目真实提供的字段，不把缺失值当0/false。先用集合按索引选择，或在子流程连接当前Item。数值、布尔、文本输出按真实类型分别有效。") ]public ItemValueField Field{get;init;}}
[SubflowSafe]
public sealed class ItemValueNode:NodeBase<ItemValueOptions>
{
    public override NodeDescriptor Descriptor=>Describe("ItemValue",[new("Item",typeof(VisionItem))],[new("Number",typeof(double),false),new("Boolean",typeof(bool),false),new("Text",typeof(string),false)]);
    protected override IReadOnlyList<string> Validate(ItemValueOptions p)=>Enum.IsDefined(p.Field)?[]:["字段选择无效。"];
    public override ValueTask<NodeOutput> ExecuteAsync(NodeExecutionContext c,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var item=c.Input<VisionItem>("Item");var field=c.Parameters<ItemValueOptions>().Field.ToString();
        if(!item.Fields.TryGetValue(field,out var value))throw new ArgumentException($"{item.Kind}未提供字段{field}；实际可用：{string.Join(", ",item.Fields.Keys)}。");
        var data=new Dictionary<string,object>();if(value.Number is{}n)data.Add("Number",n);else if(value.Boolean is{}b)data.Add("Boolean",b);else if(value.Text is{}t)data.Add("Text",t);else throw new ArgumentException("项目字段没有有效的类型值。");
        return ValueTask.FromResult(new NodeOutput(data));
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Contracts.Collections;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;
using VisionPlatform.Plugins.Builtin;

namespace VisionPlatform.Editor.Wpf.Parameters;
/// <summary>Chinese field picker with stable persisted keys. Never silently changes an existing selection.</summary>
public sealed class SingleFieldSelector:UserControl
{
    private readonly ParameterValue _field;private readonly IEditorSession _session;private readonly EditorNode _node;private readonly ComboBox _choices=new(){DisplayMemberPath="Label"};private bool _refreshing;
    public SingleFieldSelector(ParameterValue field,IEditorSession session,EditorNode node)
    {_field=field;_session=session;_node=node;var panel=new StackPanel();Content=panel;panel.Children.Add(_choices);var text=new TextBox{DataContext=field,Margin=new(0,4,0,0),ToolTip="稳定字段键保留旧参数兼容；优先从上面的中文字段列表选择。"};text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});panel.Children.Add(text);_choices.DropDownOpened+=(_,_)=>Refresh();_choices.SelectionChanged+=(_,_)=>{if(!_refreshing&&_choices.SelectedItem is DisplayFieldChoice choice)_field.Text=choice.Key;};Refresh();}
    public void Refresh()
    {
        _refreshing=true;try
        {
            var edge=_session.Connections.FirstOrDefault(c=>c.Target.Node==_node&&c.Target.Name=="Collection");IEnumerable<string>? fields=null;
            if(edge is not null&&_session.CurrentOutputs.TryGetValue(edge.Source.Node.NodeId,out var ports)&&ports.TryGetValue(edge.Source.Name,out var value))
            {var collection=value as VisionCollection??(value as CollectionDisplayResult)?.Collection;if(collection is not null)fields=CollectionFields.Supported(collection).Where(k=>collection.Items.All(i=>i.Fields.ContainsKey(k)));}
            var choices=fields is not null?fields.Distinct(StringComparer.OrdinalIgnoreCase).Select(k=>new DisplayFieldChoice(k,DisplayFieldCatalog.Label(k))).ToArray():DisplayFieldCatalog.Available(_session,_node).Where(f=>f.Key!="State").ToArray();
            _choices.ItemsSource=choices;_choices.SelectedItem=choices.FirstOrDefault(c=>c.Key.Equals(_field.Text,StringComparison.OrdinalIgnoreCase));_choices.ToolTip="不补造尚未实现的字段；没有匹配字段时请核对输入类型或先运行上游。";
        }
        finally{_refreshing=false;}
    }
}
public sealed class DelimiterEditor:UserControl
{
    public sealed record Choice(string Value,string Label);
    public DelimiterEditor(ParameterValue field)
    {var panel=new StackPanel();Content=panel;var list=new ComboBox{ItemsSource=new[]{new Choice("\\n","换行 LF"),new Choice("\\r\\n","回车换行 CRLF"),new Choice("\\r","回车 CR"),new Choice("\\t","制表符 TAB")},DisplayMemberPath="Label"};list.SelectedItem=((IEnumerable<Choice>)list.ItemsSource).FirstOrDefault(c=>c.Value==field.Text);list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is Choice c)field.Text=c.Value;};panel.Children.Add(list);var text=new TextBox{DataContext=field,Margin=new(0,4,0,0),ToolTip="也可明确设置自定义分隔符，内容中包含分隔符时应选长度前缀。"};text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});panel.Children.Add(text);}
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using VisionPlatform.Contracts.Data;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Parameters;
public sealed class LocalFilePathEditor:UserControl
{
    public LocalFilePathEditor(ParameterValue field,string resourceRoot)
    {
        var row=new DockPanel();Content=row;var browse=new Button{Content="选择…",MinHeight=30,Margin=new(5,0,0,0)};DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);
        var text=new TextBox{DataContext=field,MinWidth=80,ToolTip="本地绝对路径或工程内相对路径；不会搜索、读取或执行其他文件。"};text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});row.Children.Add(text);
        browse.Click+=(_,_)=>{var picker=new OpenFileDialog{Title="选择要读取的本地文件",Filter="文本 / JSON / CSV / 二进制|*.txt;*.json;*.csv;*.bin|所有文件|*.*",CheckFileExists=true,InitialDirectory=Directory.Exists(resourceRoot)?resourceRoot:null};if(picker.ShowDialog(Window.GetWindow(this))==true)field.Text=Path.GetFullPath(picker.FileName);};
    }
}
public sealed record JsonFieldChoice(string Pointer,string Label);
/// <summary>Reads only an already executed bounded snapshot. Selecting parameters never reads the disk.</summary>
public sealed class JsonFieldEditor:UserControl
{
    private readonly ParameterValue _field;private readonly IEditorSession _session;private readonly EditorNode _node;private readonly ComboBox _choices=new(){DisplayMemberPath="Label",MinHeight=28};private bool _refreshing;
    public JsonFieldEditor(ParameterValue field,IEditorSession session,EditorNode node)
    {
        _field=field;_session=session;_node=node;var panel=new StackPanel();Content=panel;panel.Children.Add(new TextBlock{Text="从本次读取的JSON选择字段（最多256项）",TextWrapping=TextWrapping.Wrap});panel.Children.Add(_choices);
        var text=new TextBox{DataContext=field,Margin=new(0,5,0,0)};text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});panel.Children.Add(text);
        _choices.DropDownOpened+=(_,_)=>Refresh();_choices.SelectionChanged+=(_,_)=>{if(!_refreshing&&_choices.SelectedItem is JsonFieldChoice choice)_field.Text=choice.Pointer;};Refresh();
    }
    public void Refresh()
    {
        _refreshing=true;try
        {
            var connection=_session.Connections.SingleOrDefault(c=>c.Target.Node==_node&&c.Target.Name=="Result");var choices=new List<JsonFieldChoice>();
            if(connection is not null&&_session.CurrentOutputs.TryGetValue(connection.Source.Node.NodeId,out var ports)&&ports.TryGetValue(connection.Source.Name,out var result)&&result is FileReadResult{Json:{}json})
            {
                void Walk(JsonElement e,string pointer,int depth)
                {if(choices.Count>=256||depth>32)return;choices.Add(new(pointer,(pointer.Length==0?"根":pointer)+" · "+e.ValueKind));if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject()){if(choices.Count>=256)break;Walk(p.Value,pointer+"/"+p.Name.Replace("~","~0").Replace("/","~1"),depth+1);}else if(e.ValueKind==JsonValueKind.Array){var i=0;foreach(var item in e.EnumerateArray()){if(choices.Count>=256)break;Walk(item,pointer+"/"+i++,depth+1);}}}
                Walk(json,"",0);
            }
            _choices.ItemsSource=choices;_choices.IsEnabled=choices.Count>0;_choices.SelectedItem=choices.FirstOrDefault(c=>c.Pointer==_field.Text);_choices.ToolTip=choices.Count==0?"请先运行文件读取节点，再查看本次JSON字段。":"仅选择现有字段，不执行表达式。";
        }
        finally{_refreshing=false;}
    }
}

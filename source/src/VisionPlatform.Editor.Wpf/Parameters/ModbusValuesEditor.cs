using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Plugins.Builtin;

namespace VisionPlatform.Editor.Wpf.Parameters;
/// <summary>Only edits a JSON parameter draft. It never opens a connection or writes a device.</summary>
public sealed class ModbusValuesEditor:UserControl
{
    public sealed class ValueRow{public bool Bit{get;set;}public string Word{get;set;}="";}
    private readonly ParameterValue _field;private readonly bool _bits;
    public ModbusValuesEditor(ParameterValue field,bool bits)
    {
        _field=field;_bits=bits;var panel=new StackPanel();Content=panel;var button=new Button{Content=bits?"表格编辑线圈值…":"表格编辑UInt16寄存器值…",MinHeight=30,Margin=new(0,0,0,5)};button.Click+=(_,_)=>Open();panel.Children.Add(button);var text=new TextBox{DataContext=field,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=100,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});panel.Children.Add(text);
    }
    private void Open()
    {
        var rows=new ObservableCollection<ValueRow>();try{if(_bits)foreach(var value in ModbusCommunicationNode.ParseCoils(_field.Text))rows.Add(new(){Bit=value});else foreach(var value in ModbusCommunicationNode.ParseRegisters(_field.Text))rows.Add(new(){Word=value.ToString(CultureInfo.InvariantCulture)});}catch(Exception ex){MessageBox.Show("当前值列表无效，保留原输入，请先修正："+ex.Message);return;}
        var window=new Window{Owner=Window.GetWindow(this),Title="Modbus写入参数草稿（不会通信）",Width=430,Height=520,WindowStartupLocation=WindowStartupLocation.CenterOwner};var dock=new DockPanel{Margin=new(14)};window.Content=dock;
        var hint=new TextBlock{Text=_bits?"布尔值逐项勾选；最多1968项。确认只写入草稿，不写设备。":"原始UInt16每项0..65535；最多123项。确认只写入草稿，不写设备。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,8)};DockPanel.SetDock(hint,Dock.Top);dock.Children.Add(hint);
        var footer=new StackPanel();DockPanel.SetDock(footer,Dock.Bottom);dock.Children.Add(footer);var error=new TextBlock{TextWrapping=TextWrapping.Wrap};footer.Children.Add(error);var buttons=new WrapPanel();footer.Children.Add(buttons);var add=new Button{Content="添加一项",Margin=new(0,7,7,0)};add.Click+=(_,_)=>{if(rows.Count>=(_bits?1968:123)){error.Text="达到协议数量上限。";return;}rows.Add(new());};buttons.Children.Add(add);
        var table=new DataGrid{ItemsSource=rows,CanUserAddRows=false,AutoGenerateColumns=false,EnableRowVirtualization=true};if(_bits)table.Columns.Add(new DataGridCheckBoxColumn{Header="线圈值",Binding=new Binding(nameof(ValueRow.Bit)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=new(1,DataGridLengthUnitType.Star)});else table.Columns.Add(new DataGridTextColumn{Header="UInt16原始寄存器值",Binding=new Binding(nameof(ValueRow.Word)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=new(1,DataGridLengthUnitType.Star)});dock.Children.Add(table);
        var accept=new Button{Content="确认写入草稿",Margin=new(0,7,7,0)};accept.Click+=(_,_)=>{table.CommitEdit(DataGridEditingUnit.Cell,true);table.CommitEdit(DataGridEditingUnit.Row,true);try{if(rows.Count>(_bits?1968:123))throw new ArgumentException("超过协议写入数量上限，未修改草稿。");var value=_bits?string.Join(", ",rows.Select(r=>r.Bit?"true":"false")):string.Join(", ",rows.Select(r=>r.Word));if(_bits)ModbusCommunicationNode.ParseCoils(value);else ModbusCommunicationNode.ParseRegisters(value);_field.Text=value;window.DialogResult=true;}catch(Exception ex){error.Text=ex.Message+" 保留当前输入；未修改参数。";}};buttons.Children.Add(accept);var cancel=new Button{Content="取消",Margin=new(0,7,0,0),IsCancel=true};buttons.Children.Add(cancel);window.ShowDialog();
    }
}

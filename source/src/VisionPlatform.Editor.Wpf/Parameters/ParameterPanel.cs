using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VisionPlatform.Editor.Wpf.Themes;
using VisionPlatform.Editor.Core.Models;
using VisionPlatform.Editor.Core.Parameters;
using VisionPlatform.Editor.Core.Session;

namespace VisionPlatform.Editor.Wpf.Parameters;

public sealed class ParameterPanel : UserControl
{
    public static readonly DependencyProperty SessionProperty=DependencyProperty.Register(nameof(Session),typeof(IEditorSession),typeof(ParameterPanel),new PropertyMetadata(null,OnSessionChanged));
    public IEditorSession? Session {get=>(IEditorSession?)GetValue(SessionProperty);set=>SetValue(SessionProperty,value);}
    private readonly StackPanel _fields=new() {Margin=new Thickness(12)};
    private readonly TextBlock _title=new() {FontSize=17,FontWeight=FontWeights.SemiBold,Text="选择节点编辑参数"};
    private readonly TextBlock _message=new() {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,8)};
    private readonly Button _apply=new() {Content="应用参数",Margin=new Thickness(0,4,6,4),Padding=new Thickness(14,7,14,7)};
    private readonly Dictionary<string,FrameworkElement> _fieldContainers=new();
    private EditorNode? _node;
    public ParameterDraft? Draft {get;private set;}
    private string? _draftBaseline;
    public bool HasUnappliedDraft
    {
        get
        {
            if (Draft is null) return false;
            if (!Draft.TryBuild(out var current, out _)) return true;
            return _draftBaseline is null || !JsonNode.DeepEquals(JsonNode.Parse(_draftBaseline), JsonNode.Parse(current.GetRawText()));
        }
    }
    public void DiscardDraft() => Rebuild();
    public ParameterPanel()
    {
        var layout=new DockPanel(); var header=new StackPanel {Margin=new Thickness(12,12,12,0)};
        header.Children.Add(_title);header.Children.Add(new TextBlock {Text="编辑草稿，应用后使旧结果失效",Foreground=ThemeResources.Brush(this,"StudioMutedForeground"),Margin=new Thickness(0,5,0,4)});
        DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
        var footer=new StackPanel {Margin=new Thickness(12,0,12,10)};
        var actions=new StackPanel {Orientation=Orientation.Horizontal}; actions.Children.Add(_apply);
        var defaults=new Button {Content="恢复默认",Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,4,0,4)};
        defaults.Click+=(_,_)=>{Draft?.ResetDefaults();_message.Text="已恢复草稿默认值，尚未应用。";};
        actions.Children.Add(defaults);footer.Children.Add(actions);footer.Children.Add(_message);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=_fields});Content=layout;
        _apply.Click+=(_,_)=>ApplyChanges();
    }
    private static void OnSessionChanged(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        var panel=(ParameterPanel)d;
        if(e.OldValue is IEditorSession old)old.PropertyChanged-=panel.SessionChanged;
        if(e.NewValue is IEditorSession current)current.PropertyChanged+=panel.SessionChanged;
        panel.Rebuild();
    }
    private void SessionChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(IEditorSession.SelectedNode))Rebuild();
        else if(e.PropertyName==nameof(IEditorSession.IsBusy))_apply.IsEnabled=Session is {IsBusy:false};
        else if(e.PropertyName is nameof(IEditorSession.Revision) or nameof(IEditorSession.CurrentOutputs))
            foreach(var element in _fields.Children.OfType<Panel>().SelectMany(p=>p.Children.OfType<UIElement>())){if(element is DisplayFieldSelector display)display.Refresh();else if(element is JsonFieldEditor json)json.Refresh();else if(element is SingleFieldSelector selector)selector.Refresh();}
    }
    private void NodeChanged(object? sender,PropertyChangedEventArgs e) {if(e.PropertyName==nameof(EditorNode.Parameters))Rebuild();}
    private void Rebuild()
    {
        if(_node is not null)_node.PropertyChanged-=NodeChanged;
        _node=Session?.SelectedNode;if(_node is not null)_node.PropertyChanged+=NodeChanged;
        if(Draft is not null)foreach(var old in Draft.Fields)old.PropertyChanged-=DraftValueChanged;_fieldContainers.Clear();_fields.Children.Clear();_message.Text="";Draft=null;_draftBaseline=null;_apply.IsEnabled=Session is {IsBusy:false} && _node is not null;
        if(_node is null){_title.Text="选择节点编辑参数";return;}
        _title.Text=_node.Name;
        try
        {
            Draft=new ParameterDraft(Session!.Catalog.Get(_node.TypeId),_node.Parameters);
            foreach(var section in Draft.Sections.Where(s=>s.Descriptor.Optional))
            {
                var check=new CheckBox {Content="启用 "+section.Descriptor.Label,DataContext=section,Margin=new Thickness(0,7,0,7)};
                check.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ParameterSection.Enabled)){Mode=BindingMode.TwoWay});_fields.Children.Add(check);
            }
            foreach(var field in Draft.Fields){AddField(field);field.PropertyChanged+=DraftValueChanged;}
            RefreshApplicableFields();
            AddCollectionEditors();
            if (Draft.TryBuild(out var initial, out _)) _draftBaseline = initial.GetRawText();
        }
        catch(Exception ex){_message.Text=ex.Message;_apply.IsEnabled=false;}
    }
    private void DraftValueChanged(object? sender,PropertyChangedEventArgs e)=>RefreshApplicableFields();
    private void RefreshApplicableFields(){if(Draft is null)return;foreach(var field in Draft.Fields)if(_fieldContainers.TryGetValue(field.Descriptor.Path,out var ui))ui.Visibility=Draft.IsApplicable(field)?Visibility.Visible:Visibility.Collapsed;}
    private void AddCollectionEditors()
    {
        if (_node is null || Session is null) return;
        if (_node.TypeId == "ForEach")
        {
            var edit = new Button { Content = "编辑逐项子流程…", Margin = new Thickness(0, 8, 0, 12), MinHeight = 34 };
            edit.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "EditSubflow");
            edit.Click += async (_, _) =>
            {
                try
                {
                    if (HasUnappliedDraft && !ApplyChanges()) return;
                    var node = _node!; var p = System.Text.Json.JsonSerializer.Deserialize<VisionPlatform.Contracts.Collections.ForEachOptions>(node.Parameters, VisionPlatform.Contracts.Nodes.JsonDefaults.Options)!;
                    var window = new VisionPlatform.Editor.Wpf.Collections.SubflowEditorWindow(Session.Registry, Session.ResourceRoot) { Owner = Window.GetWindow(this) };
                    await window.LoadBodyAsync(p.Body);
                    if (window.ShowDialog() == true && window.Result is { } body) Session.CommitParameters(node, VisionPlatform.Contracts.Nodes.JsonDefaults.Element(p with { Body = body }));
                }
                catch (Exception ex) { _message.Text = ex.Message; }
            };
            _fields.Children.Insert(0, edit);
        }
        if (_node.TypeId == "RoiCollection")
        {
            var edit = new Button { Content = "编辑ROI集合 / 加入当前绘制ROI…", Margin = new Thickness(0, 8, 0, 12), MinHeight = 34 };
            edit.Click += (_, _) =>
            {
                try
                {
                    var node = _node!; var p = System.Text.Json.JsonSerializer.Deserialize<VisionPlatform.Plugins.Builtin.RoiCollectionOptions>(node.Parameters, VisionPlatform.Contracts.Nodes.JsonDefaults.Options)!;
                    var dialog = new VisionPlatform.Editor.Wpf.Collections.RoiCollectionEditorWindow(p.Regions, Session.EditedRoi) { Owner = Window.GetWindow(this) };
                    if (dialog.ShowDialog() == true && dialog.Result is { } regions) Session.CommitParameters(node, VisionPlatform.Contracts.Nodes.JsonDefaults.Element(p with { Regions = regions }));
                }
                catch (Exception ex) { _message.Text = ex.Message; }
            };
            _fields.Children.Add(edit);
        }
        if (_node.TypeId == "OCR") _fields.Children.Insert(0, new TextBlock { Text = "SingleLine：单行；SingleBlock：多行文本块；Auto：自动布局。eng仅适合英文/数字；中文需另行配置已审核语言模型。执行完成不等于识别成功；空文本、低置信度与引擎错误分别显示。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
    }
    private void AddField(ParameterValue field)
    {
        var container=new StackPanel {DataContext=field,Margin=new Thickness(0,0,0,11)};
        container.Children.Add(new TextBlock {Text=_node?.TypeId=="CollectionDisplay" && field.Descriptor.Path=="Fields" ? "显示内容与顺序" : field.Descriptor.Label,ToolTip=field.Descriptor.Description,Margin=new Thickness(0,0,0,4)});
        if(field.Descriptor.Nullable)
        {
            var use=new CheckBox {Content="启用可选值（关闭时使用自动设置）",Margin=new Thickness(0,0,0,4)};
            use.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ParameterValue.UseValue)){Mode=BindingMode.TwoWay});container.Children.Add(use);
        }
        FrameworkElement editor;
        if (_node?.TypeId == "CollectionDisplay" && field.Descriptor.Path == "Fields")
            editor = new DisplayFieldSelector(field, Session!, _node);
        else if(_node?.TypeId is "CollectionFilter" or "CollectionSort"&&field.Descriptor.Path=="Field")editor=new SingleFieldSelector(field,Session!,_node);
        else if(field.Descriptor.Path.EndsWith("Delimiter",StringComparison.Ordinal)&&field.Descriptor.ValueType==typeof(string))editor=new DelimiterEditor(field);
        else if(_node?.TypeId=="ModbusCommunication"&&field.Descriptor.Path is "CoilValues" or "RegisterValues")editor=new ModbusValuesEditor(field,field.Descriptor.Path=="CoilValues");
        else if(_node?.TypeId=="FileRead"&&field.Descriptor.Path=="Path")editor=new LocalFilePathEditor(field,Session!.ResourceRoot);
        else if(_node?.TypeId=="FileValue"&&field.Descriptor.Path=="Pointer")editor=new JsonFieldEditor(field,Session!,_node);
        else if(field.Descriptor.Path is "ValuesText" or "MatrixText")editor=new MatrixTableEditor(field,Draft!,_node!.TypeId);
        else if (field.Descriptor.Kind == ParameterKind.String && field.Descriptor.Path.EndsWith("Color", StringComparison.Ordinal))
            editor = new ColorParameterEditor(field);
        else if (_node?.TypeId == "CollectionDisplay" && field.Descriptor.Kind is ParameterKind.Integer or ParameterKind.Number)
            editor = new DisplayNumberEditor(field);
        else if(field.Descriptor.Kind==ParameterKind.Boolean)
        {
            var flag=new CheckBox {Content="启用",Padding=new Thickness(3)};
            flag.SetBinding(CheckBox.IsCheckedProperty,new Binding(nameof(ParameterValue.BooleanValue)){Mode=BindingMode.TwoWay});editor=flag;
        }
        else if(field.Descriptor.Kind==ParameterKind.Enum)
        {
            var list=new ComboBox {ItemsSource=EnumPresentation.Choices(field.Descriptor),DisplayMemberPath="Label",SelectedValuePath="Key",MinWidth=130,Padding=new Thickness(5),HorizontalAlignment=HorizontalAlignment.Stretch};
            list.SetBinding(ComboBox.SelectedValueProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
            list.SelectionChanged += (_, _) => list.ToolTip = (list.SelectedItem as EnumChoice)?.Description; editor=list;
        }
        else
        {
            var text=new TextBox {Padding=new Thickness(7,5,7,5),MinWidth=120,ToolTip=field.Descriptor.Path};
            text.SetBinding(TextBox.TextProperty,new Binding(nameof(ParameterValue.Text)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});editor=text;
        }
        editor.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty,"Parameter."+field.Descriptor.Path);
        if(field.Descriptor.Nullable)editor.SetBinding(IsEnabledProperty,new Binding(nameof(ParameterValue.UseValue)));
        container.Children.Add(editor);
        if(field.RangeHint.Length>0)container.Children.Add(new TextBlock {Text=field.RangeHint,FontSize=11,Foreground=ThemeResources.Brush(this,"StudioMutedForeground"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,0)});
        _fieldContainers[field.Descriptor.Path]=container;
        _fields.Children.Add(container);
    }
    public bool ApplyChanges()
    {
        if(Draft is null||_node is null||Session is null)return false;
        if(!Draft.TryBuild(out var parameters,out var errors)){_message.Text=string.Join("\n",errors);return false;}
        try
        {
            var validation=Session.Registry.Create(_node.TypeId).ValidateParameters(parameters);
            if(validation.Count>0){_message.Text=string.Join("\n",validation);return false;}
            Session.CommitParameters(_node,parameters);_message.Text="参数已应用；旧执行结果已失效。";return true;
        }
        catch(Exception ex){_message.Text=ex.Message;return false;}
    }
}

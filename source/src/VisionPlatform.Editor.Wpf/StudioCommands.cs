using System.Windows.Input;
namespace VisionPlatform.Editor.Wpf;

/// <summary>Buttons, menus and keyboard gestures route through the same command bindings.</summary>
public static class StudioCommands
{
    public static RoutedUICommand New { get; } = Create("新建工程", nameof(New), Key.N, ModifierKeys.Control);
    public static RoutedUICommand Open { get; } = Create("打开工程", nameof(Open), Key.O, ModifierKeys.Control);
    public static RoutedUICommand Save { get; } = Create("保存工程", nameof(Save), Key.S, ModifierKeys.Control);
    public static RoutedUICommand SaveAs { get; } = Create("另存为", nameof(SaveAs), Key.S, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand Run { get; } = Create("运行流程", nameof(Run), Key.F5);
    public static RoutedUICommand RunSingle { get; } = Create("运行此节点", nameof(RunSingle), Key.F6);
    public static RoutedUICommand RunTo { get; } = new("运行到此处", nameof(RunTo), typeof(StudioCommands));
    public static RoutedUICommand Cancel { get; } = new("取消运行", nameof(Cancel), typeof(StudioCommands));
    private static RoutedUICommand Create(string text, string name, Key key, ModifierKeys modifiers = ModifierKeys.None)
        => new(text, name, typeof(StudioCommands), new InputGestureCollection { new KeyGesture(key, modifiers) });
}

using System.Globalization;
using VisionPlatform.Editor.Core.Common;

namespace VisionPlatform.Editor.Wpf.AI;

/// <summary>Display strings never write into the original annotation until explicit validation/apply.</summary>
public sealed class AiAnnotationDraft : ObservableObject
{
    private string _x = "", _y = "", _width = "", _height = "";
    private bool _loading;
    private readonly Dictionary<string, string> _displayed = [];
    private readonly HashSet<string> _edited = [];
    public event Action? Changed;
    public string XText { get => _x; set => Change(ref _x, value, nameof(XText)); }
    public string YText { get => _y; set => Change(ref _y, value, nameof(YText)); }
    public string WidthText { get => _width; set => Change(ref _width, value, nameof(WidthText)); }
    public string HeightText { get => _height; set => Change(ref _height, value, nameof(HeightText)); }
    public bool HasChanges => _edited.Count > 0;
    private void Change(ref string storage, string value, string name)
    {
        if (!Set(ref storage, value, name) || _loading) return;
        if (_displayed.GetValueOrDefault(name) == value) _edited.Remove(name); else _edited.Add(name);
        Changed?.Invoke();
    }
    public void Load(AiBox? box)
    {
        _loading = true;
        try
        {
            XText = Format(box?.X); YText = Format(box?.Y); WidthText = Format(box?.Width); HeightText = Format(box?.Height);
            _displayed.Clear(); _displayed[nameof(XText)] = XText; _displayed[nameof(YText)] = YText; _displayed[nameof(WidthText)] = WidthText; _displayed[nameof(HeightText)] = HeightText; _edited.Clear();
        }
        finally { _loading = false; }
    }
    private static string Format(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
    public AiBox Prepare(AiBox original)
    {
        double Value(string property, string text, double unchanged, string label)
            => !_edited.Contains(property) ? unchanged : AiJson.Parse(text, label, 0, 100000000);
        var candidate = original.Copy();
        candidate.X = Value(nameof(XText), XText, original.X, "X"); candidate.Y = Value(nameof(YText), YText, original.Y, "Y");
        candidate.Width = Value(nameof(WidthText), WidthText, original.Width, "宽度"); candidate.Height = Value(nameof(HeightText), HeightText, original.Height, "高度");
        return candidate;
    }
}

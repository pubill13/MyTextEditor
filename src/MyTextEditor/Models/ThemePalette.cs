using System.Windows;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MyTextEditor.Models;

public sealed class ThemePalette
{
    private ThemePalette(string id, string name, bool dark, string window, string surface, string alternate,
        string border, string text, string muted, string accent, string hover, string selection,
        string editorSelection, string inactiveSelection, string currentLine, string thumb)
    {
        Id = id; Name = name; IsDark = dark;
        Window = Color(window); EditorBackground = Color(surface); Alternate = Color(alternate);
        Border = Color(border); EditorForeground = Color(text); MarginForeground = Color(muted);
        Accent = Color(accent); Hover = Color(hover); Selection = Color(selection);
        SelectionBackground = Color(editorSelection); SelectionForeground = DrawingColor.White;
        InactiveSelection = Color(inactiveSelection); CurrentLine = Color(currentLine);
        ScrollThumb = Color(thumb);
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsDark { get; }
    private DrawingColor Window { get; }
    private DrawingColor Alternate { get; }
    private DrawingColor Border { get; }
    private DrawingColor Hover { get; }
    private DrawingColor Selection { get; }
    public DrawingColor EditorBackground { get; }
    public DrawingColor EditorForeground { get; }
    public DrawingColor MarginBackground => Window;
    public DrawingColor MarginForeground { get; }
    public DrawingColor CurrentLine { get; }
    public DrawingColor SelectionBackground { get; }
    public DrawingColor SelectionForeground { get; }
    public DrawingColor InactiveSelection { get; }
    public DrawingColor InactiveSelectionForeground => IsDark ? DrawingColor.White : EditorForeground;
    public DrawingColor ScrollTrack => Alternate;
    public DrawingColor ScrollThumb { get; }
    public DrawingColor ScrollHover => Hover;
    public DrawingColor Accent { get; }
    public DrawingColor UiSelection => Selection;

    public static IReadOnlyList<ThemePalette> All { get; } = new[]
    {
        new ThemePalette("Light", "라이트", false, "F3F5F8", "FFFFFF", "F7F9FC", "CDD5E1", "202631", "667085", "2563EB", "1D4ED8", "DCE8FF", "2459B2", "9BAFCE", "F1F4F9", "9AA7B9"),
        new ThemePalette("Dark", "다크", true, "11151C", "191F29", "202734", "354052", "ECF0F6", "A3AEC2", "5B8DEF", "79A2F2", "294677", "3566AE", "485773", "232D3C", "526078"),
        new ThemePalette("DarkPlus", "Dark+", true, "181818", "1E1E1E", "252526", "454545", "D4D4D4", "A6A6A6", "4DAAFC", "75BEFF", "264F78", "285F91", "454F5C", "292929", "5A5A5A"),
        new ThemePalette("OneDark", "One Dark", true, "21252B", "282C34", "2C313A", "4B5263", "ABB2BF", "939BAD", "61AFEF", "84C6FA", "3E4A61", "3C608A", "485061", "303640", "596273"),
        new ThemePalette("SolarizedLight", "Solarized Light", false, "EEE8D5", "FDF6E3", "F5EFDC", "CEC8B6", "586E75", "657B83", "2178AC", "17638E", "DFE9DA", "2677A5", "B0C4C2", "F2EDDA", "9CAAA5")
    };

    public static ThemePalette Get(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        Add("Window", Window); Add("Surface", EditorBackground); Add("SurfaceAlt", Alternate);
        Add("Border", Border); Add("Text", EditorForeground); Add("Muted", MarginForeground);
        Add("Accent", Accent); Add("AccentHover", Hover); Add("Selection", Selection);
        Add("Danger", Color(IsDark ? "FF6B78" : "C52A3A"));
        Add("ScrollTrack", ScrollTrack); Add("ScrollThumb", ScrollThumb); Add("ScrollHover", ScrollHover);
        return resources;

        void Add(string key, DrawingColor value)
        {
            var color = MediaColor.FromRgb(value.R, value.G, value.B);
            resources[key + "Color"] = color;
            var brush = new SolidColorBrush(color); brush.Freeze();
            resources[key + "Brush"] = brush;
        }
    }

    private static DrawingColor Color(string rgb) => DrawingColor.FromArgb(255,
        Convert.ToInt32(rgb[..2], 16), Convert.ToInt32(rgb[2..4], 16), Convert.ToInt32(rgb[4..6], 16));
}

namespace MyTextEditor.Models;

public sealed class UserSettings
{
    public string Theme { get; set; } = "Light";
    public string EditorFontFamily { get; set; } = "Cascadia Mono";
    public double EditorFontSize { get; set; } = 15;
    public double WindowWidth { get; set; } = 1380;
    public double WindowHeight { get; set; } = 860;
    public double ToolPanelWidth { get; set; } = 360;
    public double ResultPanelHeight { get; set; } = 220;
    public bool ToolPanelVisible { get; set; } = true;
    public bool ResultPanelVisible { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public string WindowState { get; set; } = "Normal";
    public List<string> RecentFiles { get; set; } = [];
    public List<string> FavoriteToolIds { get; set; } =
    [
        TextToolIds.RemoveLinesContaining,
        TextToolIds.Replace
    ];
    public List<SavedSearch> RecentSearches { get; set; } = [];
    public SearchInputState SearchState { get; set; } = new();
    public TransformInputState TransformState { get; set; } = new();
    public DiffUserSettings Diff { get; set; } = new();
}

public sealed class DiffUserSettings
{
    public bool IgnoreWhitespace { get; set; }
    public bool IgnoreCase { get; set; }
    public bool IgnoreEmptyLines { get; set; }
    public bool ScrollSync { get; set; } = true;
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 860;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}

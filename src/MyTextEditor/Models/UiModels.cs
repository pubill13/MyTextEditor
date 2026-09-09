using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyTextEditor.Models;

public sealed class ConditionEditorNode : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private bool _isExcluded;
    private bool _matchAll = true;
    public bool IsGroup { get; init; }
    public ObservableCollection<ConditionEditorNode> Children { get; } = [];
    public string Text { get => _text; set { _text = value; OnPropertyChanged(); } }
    public bool IsExcluded { get => _isExcluded; set { _isExcluded = value; OnPropertyChanged(); } }
    public bool MatchAll { get => _matchAll; set { _matchAll = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record SearchResultRow(int LineNumber, string Text, bool IsContext = false);
public sealed record TransformPreviewRow(int LineNumber, string Before, string After, string Status);
public sealed record FontChoice(string DisplayName, string FamilyName)
{
    public override string ToString() => DisplayName;
}

public sealed record ToolDescriptor(string Id, string DisplayName, int? OperationIndex = null, string? QuickOperation = null);

public sealed class SearchSnapshot(DocumentViewModel source, long revision, string text, string newLine)
{
    public DocumentViewModel Source { get; } = source;
    public long Revision { get; } = revision;
    public string Text { get; } = text;
    public string NewLine { get; } = newLine;
    public int ReferenceCount { get; set; }
}

public sealed class SearchResultSession
{
    public required string Title { get; init; }
    public required string ToolTip { get; init; }
    public required string ConditionSummary { get; init; }
    public required SearchSnapshot Snapshot { get; init; }
    public required IReadOnlyList<int> MatchedLineNumbers { get; init; }
    public ObservableCollection<SearchResultRow> Rows { get; } = [];
    public bool IncludeLineNumbers { get; set; }
}

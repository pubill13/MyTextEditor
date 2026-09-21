using System.Collections.ObjectModel;

namespace MyTextEditor.Models;

public sealed record SearchResultRow(int LineNumber, string Text, bool IsContext = false)
{
    public SearchResultSource? Source { get; init; }
    public string SourceLabel => Source?.DisplayName ?? "";
}

public sealed class SearchResultSource
{
    public required string DisplayName { get; init; }
    public string? FilePath { get; init; }
    public SearchSnapshot? Snapshot { get; init; }
    public long FileLength { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
}
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
    public SearchSnapshot? Snapshot { get; init; }
    public IReadOnlyList<int> MatchedLineNumbers { get; init; } = [];
    public string? Description { get; init; }
    public string NewLine => Snapshot?.NewLine ?? Environment.NewLine;
    public ObservableCollection<SearchResultRow> Rows { get; } = [];
    public bool IncludeLineNumbers { get; set; }
}

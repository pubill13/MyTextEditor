namespace MyTextEditor.Core.Models;

public enum TrimOperation
{
    RemoveBefore,
    RemoveAfter,
    RemoveBetween,
    KeepBetween
}

public enum CharacterRemovalSide
{
    Left,
    Right
}

public sealed record TrimOptions(
    TrimOperation Operation,
    string StartMarker,
    string? EndMarker = null,
    bool KeepStartMarker = true,
    bool KeepEndMarker = true,
    bool MatchCase = false);

public enum TextChangeStatus
{
    Changed,
    Skipped,
    Unchanged
}

public sealed record TextChangePreview(
    int LineNumber,
    string OriginalText,
    string ResultText,
    TextChangeStatus Status);

public sealed record TextTransformSummary(
    int ChangedLines,
    int SkippedLines,
    int EmptyResultLines);

public sealed record TextTransformResult(
    string Text,
    IReadOnlyList<TextChangePreview> Preview,
    TextTransformSummary Summary);

public sealed record LogCleanupSummary(
    int AnsiSequencesRemoved,
    int ControlCharactersRemoved,
    int TrailingWhitespaceCharactersRemoved,
    int CollapsedBlankLines);

public sealed record LogCleanupResult(
    TextTransformResult TransformResult,
    LogCleanupSummary CleanupSummary);

namespace MyTextEditor.Core.Models;

public enum DiffSide
{
    Left,
    Right
}

public sealed record DiffOptions(
    bool IgnoreWhitespace = false,
    bool IgnoreCase = false,
    bool IgnoreEmptyLines = false);

public enum DiffBlockKind
{
    Added,
    Deleted,
    Modified
}

public sealed record InlineDiffSpan(int Start, int Length);

public sealed record DiffLinePair(
    int? LeftLineNumber,
    string? LeftText,
    IReadOnlyList<InlineDiffSpan> LeftChanges,
    int? RightLineNumber,
    string? RightText,
    IReadOnlyList<InlineDiffSpan> RightChanges);

public sealed record DiffBlock(
    int Index,
    DiffBlockKind Kind,
    int LeftStartLine,
    int LeftLineCount,
    int RightStartLine,
    int RightLineCount,
    IReadOnlyList<DiffLinePair> Lines,
    bool IsTerminalNewLineChange = false,
    bool LeftHasTerminalNewLine = false,
    bool RightHasTerminalNewLine = false);

public sealed record DiffLineAnchor(int LeftLineNumber, int RightLineNumber);

public sealed record TextDiffResult(
    IReadOnlyList<DiffBlock> Blocks,
    IReadOnlyList<DiffLineAnchor> ScrollAnchors,
    int AddedLines,
    int DeletedLines,
    int ModifiedLines,
    bool LeftHasTerminalNewLine,
    bool RightHasTerminalNewLine)
{
    public bool HasDifferences => Blocks.Count > 0;
}

public sealed record TextMergeResult(string LeftText, string RightText, DiffSide ChangedSide);

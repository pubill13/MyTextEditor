using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class TextMergeService
{
    public TextMergeResult ApplyBlock(string left, string right, DiffBlock block, DiffSide sourceSide,
        string? leftNewLine = null, string? rightNewLine = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(block);
        leftNewLine = ResolveNewLine(left, leftNewLine);
        rightNewLine = ResolveNewLine(right, rightNewLine);

        if (block.IsTerminalNewLineChange)
        {
            var sourceHasTerminal = sourceSide == DiffSide.Left
                ? block.LeftHasTerminalNewLine : block.RightHasTerminalNewLine;
            if (sourceSide == DiffSide.Left)
                right = SetTerminalNewLine(right, rightNewLine, sourceHasTerminal);
            else
                left = SetTerminalNewLine(left, leftNewLine, sourceHasTerminal);
        }
        else if (sourceSide == DiffSide.Left)
        {
            right = ReplaceLineRange(right, rightNewLine, block.RightStartLine, block.RightLineCount,
                SliceLines(left, block.LeftStartLine, block.LeftLineCount));
        }
        else
        {
            left = ReplaceLineRange(left, leftNewLine, block.LeftStartLine, block.LeftLineCount,
                SliceLines(right, block.RightStartLine, block.RightLineCount));
        }

        return new TextMergeResult(left, right,
            sourceSide == DiffSide.Left ? DiffSide.Right : DiffSide.Left);
    }

    public TextMergeResult ApplyAll(string left, string right, DiffSide sourceSide,
        string? leftNewLine = null, string? rightNewLine = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        leftNewLine = ResolveNewLine(left, leftNewLine);
        rightNewLine = ResolveNewLine(right, rightNewLine);
        if (sourceSide == DiffSide.Left)
            right = ConvertNewLines(left, rightNewLine);
        else
            left = ConvertNewLines(right, leftNewLine);
        return new TextMergeResult(left, right,
            sourceSide == DiffSide.Left ? DiffSide.Right : DiffSide.Left);
    }

    private static IReadOnlyList<string> SliceLines(string text, int startLine, int lineCount)
    {
        if (lineCount == 0) return Array.Empty<string>();
        var lines = GetContentLines(text);
        if (startLine < 1 || startLine - 1 + lineCount > lines.Count)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Diff block no longer matches the source text.");
        return lines.Skip(startLine - 1).Take(lineCount).ToArray();
    }

    private static string ReplaceLineRange(string target, string newLine, int startLine, int lineCount,
        IReadOnlyList<string> replacement)
    {
        var terminal = TextLines.EndsWithNewLine(target);
        var lines = GetContentLines(target).ToList();
        var index = startLine - 1;
        if (index < 0 || index > lines.Count || index + lineCount > lines.Count)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Diff block no longer matches the target text.");
        lines.RemoveRange(index, lineCount);
        lines.InsertRange(index, replacement);
        if (lines.Count == 0) return string.Empty;
        var result = string.Join(newLine, lines);
        return terminal ? result + newLine : result;
    }

    private static IReadOnlyList<string> GetContentLines(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();
        var lines = TextLines.Split(text);
        return TextLines.EndsWithNewLine(text) ? lines.Take(lines.Count - 1).ToArray() : lines;
    }

    private static string ConvertNewLines(string text, string targetNewLine)
    {
        if (text.Length == 0) return string.Empty;
        var terminal = TextLines.EndsWithNewLine(text);
        var result = string.Join(targetNewLine, GetContentLines(text));
        return terminal ? result + targetNewLine : result;
    }

    private static string SetTerminalNewLine(string text, string newLine, bool enabled)
    {
        if (text.Length == 0) return enabled ? newLine : string.Empty;
        var withoutTerminal = text.EndsWith("\r\n", StringComparison.Ordinal)
            ? text[..^2]
            : TextLines.EndsWithNewLine(text) ? text[..^1] : text;
        return enabled ? withoutTerminal + newLine : withoutTerminal;
    }

    private static string ResolveNewLine(string text, string? requested)
    {
        var value = requested ?? TextLines.DetectNewLine(text);
        if (value is not ("\r\n" or "\n" or "\r"))
            throw new ArgumentException("New line must be CRLF, LF, or CR.", nameof(requested));
        return value;
    }
}

using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class TextDiffEngine
{
    public TextDiffResult Compare(string left, string right, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= new DiffOptions();

        var leftDocument = DiffDocument.Parse(left, options);
        var rightDocument = DiffDocument.Parse(right, options);
        // Diff windows may recalculate concurrently while older generations finish.
        // Keep each comparison isolated instead of sharing DiffPlex's singleton builder.
        var builder = new SideBySideDiffBuilder(new Differ());
        var model = builder.BuildDiffModel(
            leftDocument.ComparisonText, rightDocument.ComparisonText, false, false);

        var blocks = new List<DiffBlock>();
        var anchors = new List<DiffLineAnchor>();
        var pending = new List<DiffLinePair>();
        var leftAnchor = 1;
        var rightAnchor = 1;

        void CompleteBlock()
        {
            if (pending.Count == 0) return;

            var leftNumbers = pending.Where(line => line.LeftLineNumber.HasValue)
                .Select(line => line.LeftLineNumber!.Value).ToArray();
            var rightNumbers = pending.Where(line => line.RightLineNumber.HasValue)
                .Select(line => line.RightLineNumber!.Value).ToArray();
            var leftStart = leftNumbers.Length > 0 ? leftNumbers[0] : leftAnchor;
            var rightStart = rightNumbers.Length > 0 ? rightNumbers[0] : rightAnchor;
            var leftCount = leftNumbers.Length > 0 ? leftNumbers[^1] - leftStart + 1 : 0;
            var rightCount = rightNumbers.Length > 0 ? rightNumbers[^1] - rightStart + 1 : 0;
            var kind = leftCount == 0 ? DiffBlockKind.Added
                : rightCount == 0 ? DiffBlockKind.Deleted
                : DiffBlockKind.Modified;
            blocks.Add(new DiffBlock(blocks.Count, kind, leftStart, leftCount,
                rightStart, rightCount, pending.ToArray()));
            pending.Clear();
        }

        var rowCount = Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count);
        for (var row = 0; row < rowCount; row++)
        {
            var oldPiece = model.OldText.Lines[row];
            var newPiece = model.NewText.Lines[row];
            var unchanged = oldPiece.Type == ChangeType.Unchanged && newPiece.Type == ChangeType.Unchanged;
            if (unchanged)
            {
                CompleteBlock();
                var leftLine = leftDocument.GetOriginalLine(oldPiece.Position);
                var rightLine = rightDocument.GetOriginalLine(newPiece.Position);
                if (leftLine is not null && rightLine is not null)
                {
                    anchors.Add(new DiffLineAnchor(leftLine.Number, rightLine.Number));
                    leftAnchor = leftLine.Number + 1;
                    rightAnchor = rightLine.Number + 1;
                }
                continue;
            }

            var oldLine = leftDocument.GetOriginalLine(oldPiece.Position);
            var newLine = rightDocument.GetOriginalLine(newPiece.Position);
            if (oldLine is not null) leftAnchor = oldLine.Number + 1;
            if (newLine is not null) rightAnchor = newLine.Number + 1;
            var (leftChanges, rightChanges) = GetInlineChanges(oldLine?.Text, newLine?.Text,
                options.IgnoreWhitespace, options.IgnoreCase, builder);
            pending.Add(new DiffLinePair(oldLine?.Number, oldLine?.Text, leftChanges,
                newLine?.Number, newLine?.Text, rightChanges));
        }
        CompleteBlock();

        if (leftDocument.HasTerminalNewLine != rightDocument.HasTerminalNewLine)
        {
            blocks.Add(new DiffBlock(blocks.Count,
                leftDocument.HasTerminalNewLine ? DiffBlockKind.Deleted : DiffBlockKind.Added,
                leftDocument.AllLines.Count + 1, 0, rightDocument.AllLines.Count + 1, 0,
                Array.Empty<DiffLinePair>(), true,
                leftDocument.HasTerminalNewLine, rightDocument.HasTerminalNewLine));
        }

        var added = blocks.Where(block => block.Kind == DiffBlockKind.Added)
            .Sum(block => block.Lines.Count(line => line.RightLineNumber.HasValue));
        var deleted = blocks.Where(block => block.Kind == DiffBlockKind.Deleted)
            .Sum(block => block.Lines.Count(line => line.LeftLineNumber.HasValue));
        var modified = blocks.Where(block => block.Kind == DiffBlockKind.Modified)
            .Sum(block => Math.Max(
                block.Lines.Count(line => line.LeftLineNumber.HasValue),
                block.Lines.Count(line => line.RightLineNumber.HasValue)));
        return new TextDiffResult(blocks, anchors, added, deleted, modified,
            leftDocument.HasTerminalNewLine, rightDocument.HasTerminalNewLine);
    }

    private static (IReadOnlyList<InlineDiffSpan> Left, IReadOnlyList<InlineDiffSpan> Right)
        GetInlineChanges(string? left, string? right, bool ignoreWhitespace, bool ignoreCase,
            SideBySideDiffBuilder builder)
    {
        if (left is null || right is null)
        {
            return (left is null || left.Length == 0 ? Array.Empty<InlineDiffSpan>() : [new(0, left.Length)],
                right is null || right.Length == 0 ? Array.Empty<InlineDiffSpan>() : [new(0, right.Length)]);
        }

        var model = builder.BuildDiffModel(left, right, ignoreWhitespace, ignoreCase);
        var oldLine = model.OldText.Lines.FirstOrDefault();
        var newLine = model.NewText.Lines.FirstOrDefault();
        return (BuildSpans(oldLine?.SubPieces, true), BuildSpans(newLine?.SubPieces, false));
    }

    private static IReadOnlyList<InlineDiffSpan> BuildSpans(IReadOnlyList<DiffPiece>? pieces, bool oldSide)
    {
        if (pieces is null || pieces.Count == 0) return Array.Empty<InlineDiffSpan>();
        var spans = new List<InlineDiffSpan>();
        var offset = 0;
        foreach (var piece in pieces)
        {
            var length = piece.Text?.Length ?? 0;
            var changed = oldSide
                ? piece.Type is ChangeType.Deleted or ChangeType.Modified
                : piece.Type is ChangeType.Inserted or ChangeType.Modified;
            if (changed && length > 0)
            {
                if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == offset)
                    spans[^1] = spans[^1] with { Length = spans[^1].Length + length };
                else
                    spans.Add(new InlineDiffSpan(offset, length));
            }
            offset += length;
        }
        return spans;
    }

    private sealed record OriginalLine(int Number, string Text);

    private sealed class DiffDocument
    {
        private DiffDocument(IReadOnlyList<OriginalLine> allLines, IReadOnlyList<OriginalLine> comparedLines,
            string comparisonText, bool hasTerminalNewLine)
        {
            AllLines = allLines;
            ComparedLines = comparedLines;
            ComparisonText = comparisonText;
            HasTerminalNewLine = hasTerminalNewLine;
        }

        public IReadOnlyList<OriginalLine> AllLines { get; }
        public IReadOnlyList<OriginalLine> ComparedLines { get; }
        public string ComparisonText { get; }
        public bool HasTerminalNewLine { get; }

        public OriginalLine? GetOriginalLine(int? comparisonPosition)
        {
            if (comparisonPosition is null || comparisonPosition < 1 || comparisonPosition > ComparedLines.Count)
                return null;
            return ComparedLines[comparisonPosition.Value - 1];
        }

        public static DiffDocument Parse(string text, DiffOptions options)
        {
            var terminal = TextLines.EndsWithNewLine(text);
            var split = TextLines.Split(text);
            var count = text.Length == 0 ? 0 : split.Count - (terminal ? 1 : 0);
            // A file containing only one newline has no text-bearing line. Treat it as
            // an empty document with a terminal newline so it produces one useful block.
            if (terminal && count == 1 && split[0].Length == 0) count = 0;
            var allLines = Enumerable.Range(0, count)
                .Select(index => new OriginalLine(index + 1, split[index])).ToArray();
            var compared = options.IgnoreEmptyLines
                ? allLines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray()
                : allLines;
            var normalized = compared.Select(line => "\u0001" + Normalize(line.Text, options));
            return new DiffDocument(allLines, compared, string.Join('\n', normalized), terminal);
        }

        private static string Normalize(string text, DiffOptions options)
        {
            if (options.IgnoreWhitespace)
            {
                var builder = new StringBuilder(text.Length);
                var whitespace = false;
                foreach (var character in text.Trim(' ', '\t'))
                {
                    if (character is ' ' or '\t')
                    {
                        if (!whitespace) builder.Append(' ');
                        whitespace = true;
                    }
                    else
                    {
                        builder.Append(character);
                        whitespace = false;
                    }
                }
                text = builder.ToString();
            }
            return options.IgnoreCase ? text.ToUpperInvariant() : text;
        }
    }
}

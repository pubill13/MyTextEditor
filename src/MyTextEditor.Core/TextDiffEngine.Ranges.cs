using System.Collections;
using System.Text;
using DiffPlex;
using DiffPlex.DiffBuilder;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed partial class TextDiffEngine
{
    public static int? GetLineNumber(DiffBlock block, int index, DiffSide side)
        => block.Lines is LazyPairs ranges ? ranges.GetRange(index, side)?.Number
            : side == DiffSide.Left ? block.Lines[index].LeftLineNumber : block.Lines[index].RightLineNumber;

    public static int GetLineLength(DiffBlock block, int index, DiffSide side)
        => block.Lines is LazyPairs ranges ? ranges.GetRange(index, side)?.Length ?? 0
            : (side == DiffSide.Left ? block.Lines[index].LeftText : block.Lines[index].RightText)?.Length ?? 0;

    public sealed class DiffSnapshot
    {
        internal RangeDocument Document { get; }
        public DiffOptions Options { get; }
        public string Text { get; }
        internal DiffSnapshot(string text, DiffOptions options, CancellationToken token)
        { Text = text; Options = options; Document = new(text, options, token); }
    }

    public DiffSnapshot CreateSnapshot(string text, DiffOptions? options = null, CancellationToken cancellationToken = default)
        => new(text, options ?? new(), cancellationToken);

    public TextDiffResult CompareRanges(string left, string right, DiffOptions? options = null,
        CancellationToken cancellationToken = default)
        => CompareRanges(CreateSnapshot(left, options, cancellationToken), CreateSnapshot(right, options, cancellationToken), cancellationToken);

    public TextDiffResult CompareRanges(DiffSnapshot left, DiffSnapshot right, CancellationToken cancellationToken = default)
    {
        if (left.Options != right.Options) throw new ArgumentException("Snapshot comparison options must match.");
        var options = left.Options;
        var a = left.Document;
        var b = right.Document;
        var comparer = new LineComparer(options, cancellationToken);
        var ids = new Dictionary<LineRange, int>(comparer);
        int[] Identify(RangeDocument document)
        {
            var result = new int[document.Lines.Count];
            for (var i = 0; i < result.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = document.Lines[i];
                if (!ids.TryGetValue(line, out var id)) ids.Add(line, id = ids.Count + 1);
                result[i] = id;
            }
            return result;
        }
        var ai = Identify(a);
        var bi = Identify(b);
        var common = new HashSet<int>(ai);
        common.IntersectWith(bi);
        var am = Enumerable.Range(0, ai.Length).Where(i => common.Contains(ai[i])).ToArray();
        var bm = Enumerable.Range(0, bi.Length).Where(i => common.Contains(bi[i])).ToArray();
        var matches = new List<(int A, int B)>();
        if ((long)am.Length * bm.Length > 25_000_000)
        {
            // Bounded monotonic alignment: exact anchors, potentially coarser blocks for reordered text.
            // Array-linked occurrences avoid a Queue and backing array per unique log line.
            var positions = new Dictionary<int, int>();
            var following = new int[bm.Length];
            for (var i = bm.Length - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = bi[bm[i]];
                following[i] = positions.GetValueOrDefault(id, -1);
                positions[id] = i;
            }
            var previous = -1;
            foreach (var index in am)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!positions.TryGetValue(ai[index], out var position)) continue;
                while (position >= 0 && bm[position] <= previous)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    position = following[position];
                }
                if (position >= 0)
                {
                    previous = bm[position];
                    matches.Add((index, previous));
                    position = following[position];
                }
                positions[ai[index]] = position;
            }
        }
        else if (am.Length > 0 && bm.Length > 0)
        {
            var diff = new Differ().CreateLineDiffs(string.Join('\n', am.Select(i => ai[i])),
                string.Join('\n', bm.Select(i => bi[i])), false, false);
            cancellationToken.ThrowIfCancellationRequested();
            int x = 0, y = 0;
            foreach (var block in diff.DiffBlocks)
            {
                while (x < block.DeleteStartA && y < block.InsertStartB) matches.Add((am[x++], bm[y++]));
                x = block.DeleteStartA + block.DeleteCountA;
                y = block.InsertStartB + block.InsertCountB;
            }
            while (x < am.Length && y < bm.Length) matches.Add((am[x++], bm[y++]));
        }
        var blocks = new List<DiffBlock>();
        var anchors = new List<DiffLineAnchor> { new(1, 1) };
        int old = 0, next = 0, leftAnchor = 1, rightAnchor = 1, added = 0, deleted = 0, modified = 0;
        matches.Add((ai.Length, bi.Length));
        foreach (var (x, y) in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ac = x - old;
            var bc = y - next;
            if (ac != 0 || bc != 0)
            {
                var startA = ac == 0 ? leftAnchor : a.Lines[old].Number;
                var startB = bc == 0 ? rightAnchor : b.Lines[next].Number;
                var countA = ac == 0 ? 0 : a.Lines[x - 1].Number - startA + 1;
                var countB = bc == 0 ? 0 : b.Lines[y - 1].Number - startB + 1;
                var kind = ac == 0 ? DiffBlockKind.Added : bc == 0 ? DiffBlockKind.Deleted : DiffBlockKind.Modified;
                blocks.Add(new(blocks.Count, kind, startA, countA, startB, countB, new LazyPairs(a, b, old, next, ac, bc)));
                anchors.Add(new(leftAnchor, rightAnchor));
                if (ac == 0) added += bc;
                else if (bc == 0) deleted += ac;
                else modified += Math.Max(ac, bc);
            }
            if (x < ai.Length && y < bi.Length)
            {
                // Only endpoints of a linear run are needed for scroll interpolation.
                var anchor = new DiffLineAnchor(a.Lines[x].Number, b.Lines[y].Number);
                if (anchors.Count > 1 && anchor.LeftLineNumber - anchors[^1].LeftLineNumber == 1 &&
                    anchor.RightLineNumber - anchors[^1].RightLineNumber == 1 &&
                    anchors[^1].LeftLineNumber - anchors[^2].LeftLineNumber ==
                    anchors[^1].RightLineNumber - anchors[^2].RightLineNumber)
                    anchors[^1] = anchor;
                else anchors.Add(anchor);
                leftAnchor = anchor.LeftLineNumber + 1;
                rightAnchor = anchor.RightLineNumber + 1;
            }
            old = x + 1;
            next = y + 1;
        }
        anchors.Add(new(a.LineCount + 1, b.LineCount + 1));
        if (a.Terminal != b.Terminal)
            blocks.Add(new(blocks.Count, a.Terminal ? DiffBlockKind.Deleted : DiffBlockKind.Added,
                a.LineCount + 1, 0, b.LineCount + 1, 0, Array.Empty<DiffLinePair>(), true, a.Terminal, b.Terminal));
        return new(blocks, anchors, added, deleted, modified, a.Terminal, b.Terminal);
    }

    public DiffLinePair ComputeInlineChanges(DiffLinePair pair, DiffOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        // Token diff on an unbounded single line can be quadratic. Keep a precise changed
        // character envelope for very long lines instead of allocating millions of tokens.
        if (Math.Max(pair.LeftText?.Length ?? 0, pair.RightText?.Length ?? 0) > 16384)
        {
            var a = pair.LeftText ?? "";
            var b = pair.RightText ?? "";
            int start = 0, tail = 0;
            while (start < Math.Min(a.Length, b.Length) && a[start] == b[start])
            {
                if ((start & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                start++;
            }
            while (tail < Math.Min(a.Length, b.Length) - start && a[^(tail + 1)] == b[^(tail + 1)])
            {
                if ((tail & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                tail++;
            }
            // Native editor positions must not point into the low half of an emoji.
            if (start > 0 && ((start < a.Length && char.IsHighSurrogate(a[start - 1]) && char.IsLowSurrogate(a[start])) ||
                (start < b.Length && char.IsHighSurrogate(b[start - 1]) && char.IsLowSurrogate(b[start])))) start--;
            if (tail > 0 && ((a.Length > tail && char.IsHighSurrogate(a[a.Length - tail - 1]) && char.IsLowSurrogate(a[a.Length - tail])) ||
                (b.Length > tail && char.IsHighSurrogate(b[b.Length - tail - 1]) && char.IsLowSurrogate(b[b.Length - tail])))) tail--;
            return pair with { LeftChanges = a.Length > start + tail ? [new(start, a.Length - start - tail)] : [],
                RightChanges = b.Length > start + tail ? [new(start, b.Length - start - tail)] : [] };
        }
        var spans = GetInlineChanges(pair.LeftText, pair.RightText, options.IgnoreWhitespace, options.IgnoreCase,
            new SideBySideDiffBuilder(new Differ()));
        cancellationToken.ThrowIfCancellationRequested();
        return pair with { LeftChanges = spans.Left, RightChanges = spans.Right };
    }

    internal readonly record struct LineRange(string Source, int Start, int Length, int Number)
    {
        public string Text => Source.Substring(Start, Length);
    }

    internal sealed class RangeDocument
    {
        public List<LineRange> Lines { get; } = new();
        public int LineCount { get; }
        public bool Terminal { get; }
        public RangeDocument(string text, DiffOptions options, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(text);
            Terminal = TextLines.EndsWithNewLine(text);
            int start = 0, number = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                if (i < text.Length && text[i] != '\r' && text[i] != '\n') continue;
                if (i == text.Length && start == i) break;
                var line = new LineRange(text, start, i - start, ++number);
                if (!options.IgnoreEmptyLines || !text.AsSpan(start, i - start).IsWhiteSpace()) Lines.Add(line);
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (Terminal && number == 1 && (text == "\n" || text == "\r" || text == "\r\n")) { Lines.Clear(); number = 0; }
            LineCount = number;
        }
    }

    private sealed class LineComparer(DiffOptions options, CancellationToken token) : IEqualityComparer<LineRange>
    {
        private int Next(LineRange line, ref int position)
        {
            if ((position & 65535) <= 1) token.ThrowIfCancellationRequested();
            var end = line.Start + line.Length;
            if (position >= end) return -1;
            var c = line.Source[position++];
            if (options.IgnoreWhitespace && c is ' ' or '\t')
            {
                while (position < end && line.Source[position] is ' ' or '\t')
                {
                    if ((position & 65535) == 0) token.ThrowIfCancellationRequested();
                    position++;
                }
                if (position == end) return -1;
                c = ' ';
            }
            if (!options.IgnoreCase) return c;
            if (char.IsHighSurrogate(c) && position < end && Rune.TryCreate(c, line.Source[position], out var rune))
            {
                position++;
                return Rune.ToUpperInvariant(rune).Value;
            }
            return char.ToUpperInvariant(c);
        }
        private int Start(LineRange line)
        {
            var i = line.Start;
            if (options.IgnoreWhitespace) while (i < line.Start + line.Length && line.Source[i] is ' ' or '\t')
            {
                if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
                i++;
            }
            return i;
        }
        public bool Equals(LineRange a, LineRange b)
        {
            var x = Start(a); var y = Start(b);
            while (x < a.Start + a.Length || y < b.Start + b.Length)
                if (Next(a, ref x) != Next(b, ref y)) return false;
            return true;
        }
        public int GetHashCode(LineRange line)
        {
            var p = Start(line); var hash = 2166136261u;
            while (p < line.Start + line.Length) { var c = Next(line, ref p); if (c != -1) hash = (hash ^ (uint)c) * 16777619; }
            return (int)hash;
        }
    }

    private sealed class LazyPairs(RangeDocument a, RangeDocument b, int startA, int startB, int countA, int countB)
        : IReadOnlyList<DiffLinePair>
    {
        public int Count => Math.Max(countA, countB);
        public LineRange? GetRange(int index, DiffSide side)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return side == DiffSide.Left
                ? index < countA ? a.Lines[startA + index] : null
                : index < countB ? b.Lines[startB + index] : null;
        }
        public DiffLinePair this[int index] => index < 0 || index >= Count ? throw new ArgumentOutOfRangeException(nameof(index)) :
            new(index < countA ? a.Lines[startA + index].Number : null, index < countA ? a.Lines[startA + index].Text : null, [],
                index < countB ? b.Lines[startB + index].Number : null, index < countB ? b.Lines[startB + index].Text : null, []);
        public IEnumerator<DiffLinePair> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

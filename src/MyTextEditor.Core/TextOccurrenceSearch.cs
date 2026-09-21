using System.Globalization;
using System.Text;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed record TextOccurrence(int Start, int Length, bool Wrapped);

public static class TextOccurrenceSearch
{
    // Offsets are UTF-16, matching .NET strings; callers translate to native editor offsets.
    public static TextOccurrence? Find(string text, string term, int startOffset,
        SearchOptions? options = null, bool backwards = false, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(term);
        if (term.Length == 0 || term.Length > text.Length) return null;
        options ??= new SearchOptions();
        startOffset = Math.Clamp(startOffset, 0, text.Length);
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!backwards)
        {
            var found = Scan(startOffset, text.Length);
            if (found >= 0) return new(found, term.Length, false);
            found = wrap ? Scan(0, startOffset) : -1;
            return found >= 0 ? new(found, term.Length, true) : null;
        }

        var previous = ScanBack(startOffset - 1, 0);
        if (previous >= 0) return new(previous, term.Length, false);
        previous = wrap ? ScanBack(text.Length - term.Length, startOffset) : -1;
        return previous >= 0 ? new(previous, term.Length, true) : null;

        int Scan(int from, int end)
        {
            while (from <= text.Length - term.Length && from < end)
            {
                var index = text.IndexOf(term, from, comparison);
                if (index < 0 || index >= end) return -1;
                if (Valid(index)) return index;
                from = index + 1;
            }
            return -1;
        }

        int ScanBack(int from, int minimum)
        {
            from = Math.Min(from, text.Length - term.Length);
            while (from >= minimum)
            {
                // LastIndexOf's startIndex is the last character in its search range.
                var index = text.LastIndexOf(term, from + term.Length - 1, comparison);
                if (index < minimum) return -1;
                if (Valid(index)) return index;
                from = index - 1;
            }
            return -1;
        }

        bool Valid(int index) => IsScalarBoundary(text, index)
            && IsScalarBoundary(text, index + term.Length)
            && (!options.WholeWord || (!WordBefore(text, index) && !WordAt(text, index + term.Length)));
    }

    private static bool IsScalarBoundary(string text, int index) => index <= 0 || index >= text.Length
        || !char.IsHighSurrogate(text[index - 1]) || !char.IsLowSurrogate(text[index]);

    private static bool WordBefore(string text, int index)
    {
        if (index == 0) return false;
        var previous = index - 1;
        if (previous > 0 && char.IsLowSurrogate(text[previous]) && char.IsHighSurrogate(text[previous - 1])) previous--;
        return WordAt(text, previous);
    }

    private static bool WordAt(string text, int index)
    {
        if (index >= text.Length || !Rune.TryGetRuneAt(text, index, out var rune)) return false;
        return Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation;
    }
}

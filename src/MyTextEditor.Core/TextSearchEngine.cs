using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class TextSearchEngine
{
    public IReadOnlyList<SearchResult> Search(string text, ConditionNode condition, SearchOptions? options = null)
    {
        var rangeResults = SearchRanges(text, condition, options);
        var results = new List<SearchResult>(rangeResults.Count);
        foreach (var result in rangeResults)
        {
            var context = new List<ContextLine>(result.Context.Count);
            foreach (var item in result.Context)
            {
                context.Add(new ContextLine(item.LineNumber,
                    text.Substring(item.Range.Start, item.Range.Length), item.IsMatch));
            }
            results.Add(new SearchResult(result.LineNumber,
                text.Substring(result.Range.Start, result.Range.Length), context));
        }
        return results;
    }

    public IReadOnlyList<SearchRangeResult> SearchRanges(
        string text, ConditionNode condition, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(condition);
        options ??= new SearchOptions();
        if (options.ContextLines < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "문맥 줄 수는 0 이상이어야 합니다.");
        if (!HasValidCondition(condition)) return [];

        var lines = GetLineRanges(text);
        var matches = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            var range = lines[index];
            if (Evaluate(condition, text.AsSpan(range.Start, range.Length), options)) matches.Add(index);
        }

        var results = new List<SearchRangeResult>(matches.Count);
        foreach (var matchIndex in matches)
        {
            var context = new List<ContextRange>();
            var from = Math.Max(0, matchIndex - options.ContextLines);
            var to = Math.Min(lines.Count - 1, matchIndex + options.ContextLines);
            for (var contextIndex = from; contextIndex <= to; contextIndex++)
            {
                context.Add(new ContextRange(contextIndex + 1, lines[contextIndex], contextIndex == matchIndex));
            }
            results.Add(new SearchRangeResult(matchIndex + 1, lines[matchIndex], context));
        }
        return results;
    }

    public bool HasValidCondition(ConditionNode condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return HasValidConditionCore(condition);
    }

    private static List<TextRange> GetLineRanges(string text)
    {
        var ranges = new List<TextRange>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            ranges.Add(new TextRange(start, index - start));
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        ranges.Add(new TextRange(start, text.Length - start));
        return ranges;
    }

    private static bool Evaluate(ConditionNode condition, ReadOnlySpan<char> line, SearchOptions options) =>
        condition switch
        {
            TextCondition item => EvaluateText(item, line, options),
            ConditionGroup group => EvaluateGroup(group, line, options),
            _ => false
        };

    private static bool EvaluateGroup(ConditionGroup group, ReadOnlySpan<char> line, SearchOptions options)
    {
        var foundValidChild = false;
        if (group.Operator == ConditionOperator.All)
        {
            foreach (var child in group.Children)
            {
                if (!HasValidConditionCore(child)) continue;
                foundValidChild = true;
                if (!Evaluate(child, line, options)) return false;
            }
            return foundValidChild;
        }

        foreach (var child in group.Children)
        {
            if (!HasValidConditionCore(child)) continue;
            if (Evaluate(child, line, options)) return true;
        }
        return false;
    }

    private static bool HasValidConditionCore(ConditionNode condition) => condition switch
    {
        TextCondition item => !string.IsNullOrWhiteSpace(item.Value),
        ConditionGroup group => HasValidChild(group.Children),
        _ => false
    };

    private static bool HasValidChild(IReadOnlyList<ConditionNode> children)
    {
        foreach (var child in children)
        {
            if (HasValidConditionCore(child)) return true;
        }
        return false;
    }

    private static bool EvaluateText(TextCondition condition, ReadOnlySpan<char> line, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(condition.Value)) return false;
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var contains = options.WholeWord
            ? ContainsWholeWord(line, condition.Value.AsSpan(), comparison)
            : line.Contains(condition.Value.AsSpan(), comparison);
        return condition.Kind == TextConditionKind.Contains ? contains : !contains;
    }

    private static bool ContainsWholeWord(
        ReadOnlySpan<char> text, ReadOnlySpan<char> value, StringComparison comparison)
    {
        var searchedThrough = 0;
        while (searchedThrough <= text.Length - value.Length)
        {
            var relativeIndex = text[searchedThrough..].IndexOf(value, comparison);
            if (relativeIndex < 0) return false;
            var index = searchedThrough + relativeIndex;
            var leftBoundary = index == 0 || !IsWordCharacter(text[index - 1]);
            var end = index + value.Length;
            var rightBoundary = end == text.Length || !IsWordCharacter(text[end]);
            if (leftBoundary && rightBoundary) return true;
            searchedThrough = index + 1;
        }
        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

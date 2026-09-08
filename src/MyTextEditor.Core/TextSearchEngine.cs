using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class TextSearchEngine
{
    public IReadOnlyList<SearchResult> Search(
        string text,
        ConditionNode condition,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(condition);
        options ??= new SearchOptions();
        if (options.ContextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "문맥 줄 수는 0 이상이어야 합니다.");
        }

        if (!HasValidCondition(condition))
        {
            return [];
        }

        var lines = TextLines.Split(text);
        var results = new List<SearchResult>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!Evaluate(condition, lines[index], options))
            {
                continue;
            }

            var context = new List<ContextLine>();
            var from = Math.Max(0, index - options.ContextLines);
            var to = Math.Min(lines.Count - 1, index + options.ContextLines);
            for (var contextIndex = from; contextIndex <= to; contextIndex++)
            {
                context.Add(new ContextLine(contextIndex + 1, lines[contextIndex], contextIndex == index));
            }

            results.Add(new SearchResult(index + 1, lines[index], context));
        }

        return results;
    }

    public bool HasValidCondition(ConditionNode condition) => condition switch
    {
        TextCondition item => !string.IsNullOrWhiteSpace(item.Value),
        ConditionGroup group => group.Children.Any(HasValidCondition),
        _ => false
    };

    private static bool Evaluate(ConditionNode condition, string line, SearchOptions options)
    {
        return condition switch
        {
            TextCondition item => EvaluateText(item, line, options),
            ConditionGroup group => EvaluateGroup(group, line, options),
            _ => false
        };
    }

    private static bool EvaluateGroup(ConditionGroup group, string line, SearchOptions options)
    {
        var validChildren = group.Children.Where(HasValidConditionStatic).ToArray();
        if (validChildren.Length == 0)
        {
            return false;
        }

        return group.Operator == ConditionOperator.All
            ? validChildren.All(child => Evaluate(child, line, options))
            : validChildren.Any(child => Evaluate(child, line, options));
    }

    private static bool HasValidConditionStatic(ConditionNode condition) => condition switch
    {
        TextCondition item => !string.IsNullOrWhiteSpace(item.Value),
        ConditionGroup group => group.Children.Any(HasValidConditionStatic),
        _ => false
    };

    private static bool EvaluateText(TextCondition condition, string line, SearchOptions options)
    {
        if (string.IsNullOrWhiteSpace(condition.Value))
        {
            return false;
        }

        var contains = options.WholeWord
            ? ContainsWholeWord(line, condition.Value, options.MatchCase)
            : line.Contains(condition.Value, options.MatchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

        return condition.Kind == TextConditionKind.Contains ? contains : !contains;
    }

    private static bool ContainsWholeWord(string text, string value, bool matchCase)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = 0;
        while (start <= text.Length - value.Length)
        {
            var index = text.IndexOf(value, start, comparison);
            if (index < 0)
            {
                return false;
            }

            var leftBoundary = index == 0 || !IsWordCharacter(text[index - 1]);
            var end = index + value.Length;
            var rightBoundary = end == text.Length || !IsWordCharacter(text[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}

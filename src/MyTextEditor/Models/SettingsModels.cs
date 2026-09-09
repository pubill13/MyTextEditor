using MyTextEditor.Core.Models;

namespace MyTextEditor.Models;

public static class TextToolIds
{
    public const string RemoveBefore = "remove-before";
    public const string RemoveAfter = "remove-after";
    public const string RemoveBetween = "remove-between";
    public const string KeepBetween = "keep-between";
    public const string RemoveCharactersLeft = "remove-characters-left";
    public const string RemoveCharactersRight = "remove-characters-right";
    public const string AddPrefix = "add-prefix";
    public const string AddSuffix = "add-suffix";
    public const string SplitByDelimiter = "split-by-delimiter";
    public const string JoinLines = "join-lines";
    public const string AddLineNumbers = "add-line-numbers";
    public const string RemoveLineNumbers = "remove-line-numbers";
    public const string RemoveLinesContaining = "remove-lines-containing";
    public const string RemoveDuplicateLines = "remove-duplicate-lines";
    public const string RemoveBlankLines = "remove-blank-lines";
    public const string CollapseBlankLines = "collapse-blank-lines";
    public const string TrimWhitespace = "trim-whitespace";
    public const string CleanupLog = "cleanup-log";
    public const string Replace = "replace";
}

public enum SavedSearchMode
{
    Simple,
    Advanced
}

public enum SavedConditionNodeType
{
    Group,
    Text
}

public enum SavedConditionOperator
{
    All,
    Any
}

public enum SavedTextConditionKind
{
    Contains,
    DoesNotContain
}

public sealed class SavedConditionNode
{
    public SavedConditionNodeType NodeType { get; set; } = SavedConditionNodeType.Group;
    public SavedConditionOperator Operator { get; set; } = SavedConditionOperator.All;
    public SavedTextConditionKind Kind { get; set; } = SavedTextConditionKind.Contains;
    public string Value { get; set; } = string.Empty;
    public List<SavedConditionNode> Children { get; set; } = [];
}

public sealed class SavedSearchOptions
{
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public int ContextLines { get; set; }
}

public sealed class SearchInputState
{
    public SavedSearchMode Mode { get; set; }
    public List<string> SimpleAllTerms { get; set; } = [];
    public List<string> SimpleAnyTerms { get; set; } = [];
    public List<string> SimpleExcludeTerms { get; set; } = [];
    public SavedConditionNode Condition { get; set; } = new();
    public SavedSearchOptions Options { get; set; } = new();

    public static SearchInputState CreateSimple(IEnumerable<string> allTerms, IEnumerable<string> anyTerms,
        IEnumerable<string> excludeTerms, SavedSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(allTerms);
        ArgumentNullException.ThrowIfNull(anyTerms);
        ArgumentNullException.ThrowIfNull(excludeTerms);

        var state = new SearchInputState
        {
            Mode = SavedSearchMode.Simple,
            SimpleAllTerms = allTerms.ToList(),
            SimpleAnyTerms = anyTerms.ToList(),
            SimpleExcludeTerms = excludeTerms.ToList(),
            Options = options ?? new SavedSearchOptions()
        };
        state.Condition = LegacySearchMigration.BuildSimpleCondition(state);
        return state;
    }
}

public sealed class SavedSearch
{
    public SearchInputState Search { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TransformInputState
{
    public string SelectedToolId { get; set; } = TextToolIds.RemoveBefore;
    public string StartMarker { get; set; } = string.Empty;
    public string EndMarker { get; set; } = string.Empty;
    public bool KeepStartMarker { get; set; } = true;
    public bool KeepEndMarker { get; set; } = true;
    public bool MarkerMatchCase { get; set; }
    public int CharacterCount { get; set; } = 1;
    public string Value { get; set; } = string.Empty;
    public bool IncludeBlankLines { get; set; }
    public int StartNumber { get; set; } = 1;
    public string LineNumberSeparator { get; set; } = ": ";
    public string ReplaceFrom { get; set; } = string.Empty;
    public string ReplaceTo { get; set; } = string.Empty;
    public string RemoveLinesContainingText { get; set; } = string.Empty;
    public bool RemoveLinesContainingMatchCase { get; set; }
}

public static class SavedSearchHistory
{
    public const int MaximumCount = 20;

    public static void AddOrMoveToFront(List<SavedSearch> searches, SavedSearch search)
    {
        ArgumentNullException.ThrowIfNull(searches);
        ArgumentNullException.ThrowIfNull(search);

        searches.RemoveAll(existing => HasSameCriteria(existing.Search, search.Search));
        searches.Insert(0, search);
        if (searches.Count > MaximumCount)
            searches.RemoveRange(MaximumCount, searches.Count - MaximumCount);
    }

    public static bool HasSameCriteria(SearchInputState left, SearchInputState right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Mode == right.Mode
            && left.Options.MatchCase == right.Options.MatchCase
            && left.Options.WholeWord == right.Options.WholeWord
            && left.Options.ContextLines == right.Options.ContextLines
            && left.SimpleAllTerms.SequenceEqual(right.SimpleAllTerms, StringComparer.Ordinal)
            && left.SimpleAnyTerms.SequenceEqual(right.SimpleAnyTerms, StringComparer.Ordinal)
            && left.SimpleExcludeTerms.SequenceEqual(right.SimpleExcludeTerms, StringComparer.Ordinal)
            && (left.Mode == SavedSearchMode.Simple || ConditionsEqual(left.Condition, right.Condition));
    }

    private static bool ConditionsEqual(SavedConditionNode left, SavedConditionNode right)
    {
        if (left.NodeType != right.NodeType)
            return false;

        if (left.NodeType == SavedConditionNodeType.Text)
            return left.Kind == right.Kind && string.Equals(left.Value, right.Value, StringComparison.Ordinal);

        return left.Operator == right.Operator
            && left.Children.Count == right.Children.Count
            && left.Children.Zip(right.Children).All(pair => ConditionsEqual(pair.First, pair.Second));
    }
}

public static class LegacySearchMigration
{
    public static bool TryConvertAdvancedToSimple(SearchInputState source, out SearchInputState simple)
    {
        ArgumentNullException.ThrowIfNull(source);
        simple = new SearchInputState();
        if (source.Mode != SavedSearchMode.Advanced || source.Condition is not
            { NodeType: SavedConditionNodeType.Group, Operator: SavedConditionOperator.All } root)
            return false;

        var allTerms = new List<string>();
        var anyTerms = new List<string>();
        var excludeTerms = new List<string>();
        var foundAnyGroup = false;

        foreach (var child in root.Children)
        {
            if (child.NodeType == SavedConditionNodeType.Text)
            {
                if (!IsSimpleTerm(child.Value)) return false;
                (child.Kind == SavedTextConditionKind.Contains ? allTerms : excludeTerms).Add(child.Value);
                continue;
            }

            if (foundAnyGroup || child.NodeType != SavedConditionNodeType.Group ||
                child.Operator != SavedConditionOperator.Any || child.Children.Count == 0)
                return false;

            foreach (var anyChild in child.Children)
            {
                if (anyChild.NodeType != SavedConditionNodeType.Text ||
                    anyChild.Kind != SavedTextConditionKind.Contains || !IsSimpleTerm(anyChild.Value))
                    return false;
                anyTerms.Add(anyChild.Value);
            }
            foundAnyGroup = true;
        }

        simple = SearchInputState.CreateSimple(allTerms, anyTerms, excludeTerms, CloneOptions(source.Options));
        return true;
    }

    public static SavedConditionNode BuildSimpleCondition(SearchInputState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var children = new List<SavedConditionNode>();
        children.AddRange(state.SimpleAllTerms.Select(value => Text(SavedTextConditionKind.Contains, value)));
        if (state.SimpleAnyTerms.Count > 0)
        {
            children.Add(new SavedConditionNode
            {
                NodeType = SavedConditionNodeType.Group,
                Operator = SavedConditionOperator.Any,
                Children = state.SimpleAnyTerms.Select(value => Text(SavedTextConditionKind.Contains, value)).ToList()
            });
        }
        children.AddRange(state.SimpleExcludeTerms.Select(value => Text(SavedTextConditionKind.DoesNotContain, value)));
        return new SavedConditionNode { NodeType = SavedConditionNodeType.Group, Operator = SavedConditionOperator.All, Children = children };
    }

    private static SavedConditionNode Text(SavedTextConditionKind kind, string value) =>
        new() { NodeType = SavedConditionNodeType.Text, Kind = kind, Value = value };

    private static SavedSearchOptions CloneOptions(SavedSearchOptions options) => new()
    {
        MatchCase = options.MatchCase,
        WholeWord = options.WholeWord,
        ContextLines = options.ContextLines
    };

    private static bool IsSimpleTerm(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Contains(',') &&
        !value.Contains('\r') && !value.Contains('\n');
}

public static class SavedConditionMapper
{
    public static SavedConditionNode FromCore(ConditionNode condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition switch
        {
            ConditionGroup group => new SavedConditionNode
            {
                NodeType = SavedConditionNodeType.Group,
                Operator = group.Operator == ConditionOperator.All ? SavedConditionOperator.All : SavedConditionOperator.Any,
                Children = group.Children.Select(FromCore).ToList()
            },
            TextCondition text => new SavedConditionNode
            {
                NodeType = SavedConditionNodeType.Text,
                Kind = text.Kind == TextConditionKind.Contains ? SavedTextConditionKind.Contains : SavedTextConditionKind.DoesNotContain,
                Value = text.Value
            },
            _ => throw new ArgumentException("지원하지 않는 검색 조건 형식입니다.", nameof(condition))
        };
    }

    public static ConditionNode ToCore(SavedConditionNode condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.NodeType switch
        {
            SavedConditionNodeType.Group => new ConditionGroup(
                condition.Operator == SavedConditionOperator.All ? ConditionOperator.All : ConditionOperator.Any,
                condition.Children.Select(ToCore).ToArray()),
            SavedConditionNodeType.Text => new TextCondition(
                condition.Kind == SavedTextConditionKind.Contains ? TextConditionKind.Contains : TextConditionKind.DoesNotContain,
                condition.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), "지원하지 않는 저장 조건 형식입니다.")
        };
    }
}

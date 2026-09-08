namespace MyTextEditor.Core.Models;

public enum ConditionOperator
{
    All,
    Any
}

public enum TextConditionKind
{
    Contains,
    DoesNotContain
}

public abstract record ConditionNode;

public sealed record ConditionGroup(
    ConditionOperator Operator,
    IReadOnlyList<ConditionNode> Children) : ConditionNode;

public sealed record TextCondition(
    TextConditionKind Kind,
    string Value) : ConditionNode;

public sealed record SearchOptions(
    bool MatchCase = false,
    bool WholeWord = false,
    int ContextLines = 0);

public sealed record ContextLine(int LineNumber, string Text, bool IsMatch);

public sealed record SearchResult(
    int LineNumber,
    string Text,
    IReadOnlyList<ContextLine> Context);

public readonly record struct TextRange(int Start, int Length);

public sealed record ContextRange(int LineNumber, TextRange Range, bool IsMatch);

public sealed record SearchRangeResult(
    int LineNumber,
    TextRange Range,
    IReadOnlyList<ContextRange> Context);

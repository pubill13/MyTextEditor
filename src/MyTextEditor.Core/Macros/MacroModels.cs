namespace MyTextEditor.Core.Macros;

public enum MacroOperation
{
    Search, DeleteTargetLines, KeepTargetLines, RemoveBefore, RemoveAfter, RemoveBetween,
    KeepBetween, RemoveCharactersLeft, RemoveCharactersRight, AddPrefix, AddSuffix,
    SplitByDelimiter, JoinLines, AddLineNumbers, RemoveLineNumbers, RemoveLinesContaining,
    Replace, TrimWhitespace, RemoveDuplicateLines, RemoveBlankLines, CollapseBlankLines, CleanupLog
}

public enum MacroTarget { WholeDocument, MatchedLines }

public sealed class MacroDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "새 매크로";
    public List<MacroStep> Steps { get; set; } = [];
}

public sealed class MacroStep
{
    public MacroOperation Operation { get; set; }
    public MacroTarget Target { get; set; }
    public bool Enabled { get; set; } = true;
    public string Value { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string EndMarker { get; set; } = "";
    public int Count { get; set; } = 1;
    public int StartNumber { get; set; } = 1;
    public bool KeepStart { get; set; } = true;
    public bool KeepEnd { get; set; } = true;
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    public bool IncludeBlankLines { get; set; }
    public List<string> AllTerms { get; set; } = [];
    public List<string> AnyTerms { get; set; } = [];
    public List<string> ExcludeTerms { get; set; } = [];
}

public sealed record MacroStepResult(int StepIndex, MacroOperation Operation, int ProcessedLines,
    int ChangedLines, int MatchedLines, int SkippedLines);

public sealed record MacroExecutionResult(string Text, IReadOnlyList<MacroStepResult> Steps);

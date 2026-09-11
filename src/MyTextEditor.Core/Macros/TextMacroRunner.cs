using MyTextEditor.Core.Models;

namespace MyTextEditor.Core.Macros;

public sealed class TextMacroRunner
{
    private readonly TextTransformService _transform = new();
    private readonly TextSearchEngine _search = new();
    private sealed record Line(string Text, bool Matched);

    public IReadOnlyList<string> Validate(MacroDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.Name)) errors.Add("매크로 이름을 입력하세요.");
        if (definition.Steps is null) return ["단계 목록이 없습니다."];
        var hasSearch = false;
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            void Error(string message) => errors.Add($"{i + 1}단계: {message}");
            if (step is null) { Error("단계가 비어 있습니다."); continue; }
            if (!Enum.IsDefined(step.Operation) || !Enum.IsDefined(step.Target))
            { Error("지원하지 않는 동작 또는 적용 대상입니다."); continue; }
            if (step.Value is null || step.Replacement is null || step.EndMarker is null ||
                step.AllTerms is null || step.AnyTerms is null || step.ExcludeTerms is null)
            { Error("입력값이 올바르지 않습니다."); continue; }
            if (step.AllTerms.Concat(step.AnyTerms).Concat(step.ExcludeTerms).Any(value => value is null))
            { Error("검색 조건에 null 값이 있습니다."); continue; }
            if (!step.Enabled) continue;
            if (step.Target == MacroTarget.MatchedLines && !hasSearch) Error("찾은 줄을 사용하기 전에 검색 단계가 필요합니다.");
            if (step.Operation == MacroOperation.Search)
            {
                var terms = step.AllTerms.Concat(step.AnyTerms).Concat(step.ExcludeTerms).ToList();
                if (terms.Count == 0 || terms.Any(string.IsNullOrWhiteSpace)) Error("비어 있지 않은 검색 조건이 필요합니다.");
                hasSearch = true;
            }
            if (step.Operation is MacroOperation.RemoveBefore or MacroOperation.RemoveAfter or
                MacroOperation.RemoveBetween or MacroOperation.KeepBetween or MacroOperation.SplitByDelimiter or
                MacroOperation.AddLineNumbers or MacroOperation.RemoveLineNumbers or MacroOperation.RemoveLinesContaining or MacroOperation.Replace
                && step.Value.Length == 0) Error("기준 텍스트 또는 구분자를 입력하세요.");
            if (step.Operation is MacroOperation.RemoveBetween or MacroOperation.KeepBetween && step.EndMarker.Length == 0)
                Error("종료 기준 텍스트를 입력하세요.");
            if (step.Operation is MacroOperation.RemoveCharactersLeft or MacroOperation.RemoveCharactersRight && step.Count < 0)
                Error("글자 수는 0 이상이어야 합니다.");
            if (step.Operation == MacroOperation.AddLineNumbers && step.StartNumber < 0)
                Error("시작 번호는 0 이상이어야 합니다.");
        }
        return errors;
    }

    public MacroExecutionResult Run(string text, string newLine, MacroDefinition definition,
        CancellationToken cancellationToken = default, IProgress<MacroStepResult>? progress = null,
        int? stopAfterStep = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (newLine is not ("\r\n" or "\n" or "\r")) throw new ArgumentException("줄바꿈 형식이 올바르지 않습니다.", nameof(newLine));
        var errors = Validate(definition);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(definition));
        cancellationToken.ThrowIfCancellationRequested();
        var terminal = TextLines.EndsWithNewLine(text);
        var lines = TextLines.Split(text).Select(value => new Line(value, false)).ToList();
        if (terminal) lines.RemoveAt(lines.Count - 1);
        var stats = new List<MacroStepResult>();
        var anyChange = false;
        for (var index = 0; index < definition.Steps.Count && (!stopAfterStep.HasValue || index <= stopAfterStep); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = definition.Steps[index];
            var processed = 0;
            var changed = 0;
            var skipped = 0;
            bool Targets(Line line) => step.Target == MacroTarget.WholeDocument || line.Matched;
            if (!step.Enabled || step.Target == MacroTarget.MatchedLines && !lines.Any(item => item.Matched)) skipped = lines.Count;
            else if (step.Operation == MacroOperation.Search)
            {
                var condition = BuildCondition(step);
                var options = new SearchOptions(step.MatchCase, step.WholeWord);
                for (var i = 0; i < lines.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targeted = Targets(lines[i]);
                    if (targeted) processed++; else skipped++;
                    lines[i] = lines[i] with { Matched = targeted && _search.SearchRanges(lines[i].Text, condition, options).Count > 0 };
                }
            }
            else
            {
                var output = new List<Line>(lines.Count);
                var seen = new HashSet<string>(step.MatchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                var previousBlank = false;
                long number = step.StartNumber;
                for (var i = 0; i < lines.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = lines[i];
                    var targeted = Targets(line);
                    if (!targeted)
                    {
                        if (step.Operation == MacroOperation.KeepTargetLines) changed++;
                        else output.Add(line);
                        skipped++;
                        previousBlank = false;
                        continue;
                    }
                    processed++;
                    if (step.Operation == MacroOperation.JoinLines)
                    {
                        var group = new List<Line> { line };
                        while (i + 1 < lines.Count && Targets(lines[i + 1]))
                        { cancellationToken.ThrowIfCancellationRequested(); group.Add(lines[++i]); processed++; }
                        var joined = string.Join(step.Value, group.Select(item => item.Text));
                        // The terminal newline is a separator too when the entire document is joined.
                        var consumedTerminal = step.Target == MacroTarget.WholeDocument && terminal;
                        if (consumedTerminal) { joined += step.Value; terminal = false; }
                        if (group.Count > 1 || joined != line.Text || consumedTerminal) changed += group.Count;
                        foreach (var value in TextLines.Split(joined)) output.Add(new Line(value, group.Any(item => item.Matched)));
                        continue;
                    }
                    var remove = step.Operation switch
                    {
                        MacroOperation.DeleteTargetLines => true,
                        MacroOperation.RemoveLinesContaining => line.Text.Contains(step.Value,
                            step.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
                        MacroOperation.RemoveDuplicateLines => !seen.Add(line.Text),
                        MacroOperation.RemoveBlankLines => string.IsNullOrWhiteSpace(line.Text),
                        MacroOperation.CollapseBlankLines => previousBlank && string.IsNullOrWhiteSpace(line.Text),
                        _ => false
                    };
                    previousBlank = string.IsNullOrWhiteSpace(line.Text);
                    if (remove) { changed++; continue; }
                    if (step.Operation is MacroOperation.KeepTargetLines or MacroOperation.RemoveLinesContaining or
                        MacroOperation.RemoveDuplicateLines or MacroOperation.RemoveBlankLines or MacroOperation.CollapseBlankLines)
                    { output.Add(line); continue; }
                    if (step.Operation == MacroOperation.AddLineNumbers && number > int.MaxValue &&
                        (step.IncludeBlankLines || !string.IsNullOrWhiteSpace(line.Text)))
                        throw new ArgumentException($"{index + 1}단계: 줄 번호가 최대값 {int.MaxValue}을 초과합니다.");
                    var result = TransformLine(line.Text, newLine, step, (int)Math.Min(number, int.MaxValue));
                    if (step.Operation == MacroOperation.AddLineNumbers && (step.IncludeBlankLines || !string.IsNullOrWhiteSpace(line.Text))) number++;
                    skipped += result.Summary.SkippedLines;
                    if (result.Text != line.Text) changed++;
                    foreach (var value in TextLines.Split(result.Text))
                    {
                        if (step.Operation == MacroOperation.CleanupLog && value.Length == 0 &&
                            output.Count > 0 && Targets(output[^1]) && output[^1].Text.Length == 0)
                        { if (result.Text == line.Text) changed++; continue; }
                        output.Add(new Line(value, line.Matched));
                    }
                }
                lines = output;
                if (lines.Count == 0)
                {
                    terminal = false;
                    lines.Add(new Line(string.Empty, false));
                }
            }
            anyChange |= changed > 0;
            var stat = new MacroStepResult(index, step.Operation, processed, changed, lines.Count(item => item.Matched), skipped);
            stats.Add(stat);
            progress?.Report(stat);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!anyChange) return new MacroExecutionResult(text, stats);
        var resultText = string.Join(newLine, lines.Select(line => line.Text));
        if (terminal && lines.Count > 0) resultText += newLine;
        return new MacroExecutionResult(resultText, stats);
    }

    private TextTransformResult TransformLine(string text, string newLine, MacroStep step, int number) => step.Operation switch
    {
        MacroOperation.RemoveBefore or MacroOperation.RemoveAfter or MacroOperation.RemoveBetween or MacroOperation.KeepBetween =>
            _transform.Trim(text, new TrimOptions(step.Operation switch
            {
                MacroOperation.RemoveBefore => TrimOperation.RemoveBefore,
                MacroOperation.RemoveAfter => TrimOperation.RemoveAfter,
                MacroOperation.RemoveBetween => TrimOperation.RemoveBetween,
                _ => TrimOperation.KeepBetween
            }, step.Value, step.EndMarker, step.KeepStart, step.KeepEnd, step.MatchCase), newLine),
        MacroOperation.RemoveCharactersLeft or MacroOperation.RemoveCharactersRight => _transform.RemoveCharacters(text, step.Count,
            step.Operation == MacroOperation.RemoveCharactersLeft ? CharacterRemovalSide.Left : CharacterRemovalSide.Right, newLine),
        MacroOperation.AddPrefix => _transform.AddPrefix(text, step.Value, step.IncludeBlankLines, newLine),
        MacroOperation.AddSuffix => _transform.AddSuffix(text, step.Value, step.IncludeBlankLines, newLine),
        MacroOperation.SplitByDelimiter => _transform.SplitByDelimiter(text, step.Value, newLine),
        MacroOperation.AddLineNumbers => _transform.AddLineNumbers(text, number, step.Value, step.IncludeBlankLines, newLine),
        MacroOperation.RemoveLineNumbers => _transform.RemoveLineNumbers(text, step.Value, newLine),
        MacroOperation.Replace => _transform.Replace(text, step.Value, step.Replacement, step.MatchCase, newLine),
        MacroOperation.TrimWhitespace => _transform.TrimWhitespace(text, newLine),
        MacroOperation.CleanupLog => _transform.CleanupLog(text, newLine).TransformResult,
        _ => throw new ArgumentException("지원하지 않는 매크로 동작입니다.")
    };

    private static ConditionNode BuildCondition(MacroStep step)
    {
        var children = step.AllTerms.Select(value => (ConditionNode)new TextCondition(TextConditionKind.Contains, value)).ToList();
        if (step.AnyTerms.Count > 0) children.Add(new ConditionGroup(ConditionOperator.Any,
            step.AnyTerms.Select(value => (ConditionNode)new TextCondition(TextConditionKind.Contains, value)).ToList()));
        children.AddRange(step.ExcludeTerms.Select(value => new TextCondition(TextConditionKind.DoesNotContain, value)));
        return new ConditionGroup(ConditionOperator.All, children);
    }
}

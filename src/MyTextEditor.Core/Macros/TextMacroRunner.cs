using MyTextEditor.Core.Models;

namespace MyTextEditor.Core.Macros;

public sealed class TextMacroRunner
{
    private readonly TextTransformService _transform = new();
    // Unchanged lines reference the source buffer instead of copying each log line.
    private readonly record struct Line(ReadOnlyMemory<char> Text, bool Matched);

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
        var lines = new List<Line>();
        AppendLines(lines, text.AsMemory(), false, cancellationToken);
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
                for (var i = 0; i < lines.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targeted = Targets(lines[i]);
                    if (targeted) processed++; else skipped++;
                    lines[i] = lines[i] with { Matched = targeted && Matches(lines[i].Text.Span, step) };
                }
            }
            else
            {
                var output = new List<Line>(lines.Count);
                var seen = step.Operation == MacroOperation.RemoveDuplicateLines
                    ? new HashSet<ReadOnlyMemory<char>>(new LineComparer(step.MatchCase)) : null;
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
                        var groupStart = i;
                        var matched = line.Matched;
                        while (i + 1 < lines.Count && Targets(lines[i + 1]))
                        { cancellationToken.ThrowIfCancellationRequested(); matched |= lines[++i].Matched; processed++; }
                        // The terminal newline is a separator too when the entire document is joined.
                        var consumedTerminal = step.Target == MacroTarget.WholeDocument && terminal;
                        var groupCount = i - groupStart + 1;
                        var joined = Join(lines, groupStart, groupCount, step.Value, consumedTerminal, cancellationToken);
                        if (consumedTerminal) terminal = false;
                        if (groupCount > 1 || !joined.AsSpan().SequenceEqual(line.Text.Span) || consumedTerminal) changed += groupCount;
                        AppendLines(output, joined.AsMemory(), matched, cancellationToken);
                        continue;
                    }
                    var remove = step.Operation switch
                    {
                        MacroOperation.DeleteTargetLines => true,
                        MacroOperation.RemoveLinesContaining => line.Text.Span.Contains(step.Value,
                            step.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase),
                        MacroOperation.RemoveDuplicateLines => !seen!.Add(line.Text),
                        MacroOperation.RemoveBlankLines => line.Text.Span.IsWhiteSpace(),
                        MacroOperation.CollapseBlankLines => previousBlank && line.Text.Span.IsWhiteSpace(),
                        _ => false
                    };
                    previousBlank = line.Text.Span.IsWhiteSpace();
                    if (remove) { changed++; continue; }
                    if (step.Operation is MacroOperation.KeepTargetLines or MacroOperation.RemoveLinesContaining or
                        MacroOperation.RemoveDuplicateLines or MacroOperation.RemoveBlankLines or MacroOperation.CollapseBlankLines)
                    { output.Add(line); continue; }
                    if (step.Operation == MacroOperation.AddLineNumbers && number > int.MaxValue &&
                        (step.IncludeBlankLines || !line.Text.Span.IsWhiteSpace()))
                        throw new ArgumentException($"{index + 1}단계: 줄 번호가 최대값 {int.MaxValue}을 초과합니다.");
                    var result = TransformLine(line.Text, newLine, step, (int)Math.Min(number, int.MaxValue));
                    if (step.Operation == MacroOperation.AddLineNumbers && (step.IncludeBlankLines || !line.Text.Span.IsWhiteSpace())) number++;
                    skipped += result.Skipped;
                    var lineChanged = !result.Text.Span.SequenceEqual(line.Text.Span);
                    if (lineChanged) changed++;
                    if (step.Operation == MacroOperation.CleanupLog)
                    {
                        if (result.Text.Length == 0 &&
                            output.Count > 0 && Targets(output[^1]) && output[^1].Text.Length == 0)
                        { if (!lineChanged) changed++; continue; }
                    }
                    AppendLines(output, result.Text, line.Matched, cancellationToken);
                }
                lines = output;
                if (lines.Count == 0)
                {
                    terminal = false;
                    lines.Add(new Line(ReadOnlyMemory<char>.Empty, false));
                }
            }
            anyChange |= changed > 0;
            var stat = new MacroStepResult(index, step.Operation, processed, changed, lines.Count(item => item.Matched), skipped);
            stats.Add(stat);
            progress?.Report(stat);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!anyChange) return new MacroExecutionResult(text, stats);
        var resultText = Join(lines, 0, lines.Count, newLine, terminal, cancellationToken);
        return new MacroExecutionResult(resultText, stats);
    }

    private (ReadOnlyMemory<char> Text, int Skipped) TransformLine(ReadOnlyMemory<char> text, string newLine, MacroStep step, int number)
    {
        var span = text.Span;
        var comparison = step.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (step.Operation)
        {
            case MacroOperation.TrimWhitespace:
                var trimmed = span.Trim();
                var left = span.Length - span.TrimStart().Length;
                return (text.Slice(left, trimmed.Length), 0);
            case MacroOperation.AddPrefix:
            case MacroOperation.AddSuffix:
            case MacroOperation.AddLineNumbers:
                if (!step.IncludeBlankLines && span.IsWhiteSpace()) return (text, 1);
                var affix = step.Operation == MacroOperation.AddLineNumbers
                    ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) + step.Value : step.Value;
                if (affix.Length == 0) return (text, 0);
                return ((step.Operation == MacroOperation.AddSuffix
                    ? string.Concat(span, affix.AsSpan()) : string.Concat(affix.AsSpan(), span)).AsMemory(), 0);
            case MacroOperation.RemoveBefore:
            case MacroOperation.RemoveAfter:
            case MacroOperation.RemoveBetween:
            case MacroOperation.KeepBetween:
                var start = span.IndexOf(step.Value, comparison);
                if (start < 0) return (text, 1);
                var afterStart = start + step.Value.Length;
                if (step.Operation == MacroOperation.RemoveBefore)
                    return (text[(step.KeepStart ? start : afterStart)..], 0);
                if (step.Operation == MacroOperation.RemoveAfter)
                    return (text[..(step.KeepStart ? afterStart : start)], 0);
                var relativeEnd = span[afterStart..].IndexOf(step.EndMarker, comparison);
                if (relativeEnd < 0) return (text, 1);
                var end = afterStart + relativeEnd;
                if (step.Operation == MacroOperation.KeepBetween)
                    return (text[(step.KeepStart ? start : afterStart)..(step.KeepEnd ? end + step.EndMarker.Length : end)], 0);
                return (string.Concat(span[..(step.KeepStart ? afterStart : start)],
                    span[(step.KeepEnd ? end : end + step.EndMarker.Length)..]).AsMemory(), 0);
            case MacroOperation.RemoveLineNumbers:
                var digitEnd = span.Length > 0 && span[0] == '-' ? 1 : 0;
                var digitStart = digitEnd;
                while (digitEnd < span.Length && span[digitEnd] is >= '0' and <= '9') digitEnd++;
                if (digitEnd == digitStart || !span[digitEnd..].StartsWith(step.Value, StringComparison.Ordinal))
                    return (text, 1);
                return (text[(digitEnd + step.Value.Length)..], 0);
            case MacroOperation.Replace:
                if (!span.Contains(step.Value, comparison)) return (text, 0);
                return (text.ToString().Replace(step.Value, step.Replacement, comparison).AsMemory(), 0);
            case MacroOperation.SplitByDelimiter:
                if (!span.Contains(step.Value, StringComparison.Ordinal)) return (text, 0);
                return (text.ToString().Replace(step.Value, newLine, StringComparison.Ordinal).AsMemory(), 0);
        }
        // Grapheme removal and ANSI cleanup retain the shared, tested transform rules.
        var result = TransformLineFallback(text.ToString(), newLine, step);
        return (result.Text.AsMemory(), result.Summary.SkippedLines);
    }

    private TextTransformResult TransformLineFallback(string text, string newLine, MacroStep step) => step.Operation switch
    {
        MacroOperation.RemoveCharactersLeft or MacroOperation.RemoveCharactersRight => _transform.RemoveCharacters(text, step.Count,
            step.Operation == MacroOperation.RemoveCharactersLeft ? CharacterRemovalSide.Left : CharacterRemovalSide.Right, newLine),
        MacroOperation.CleanupLog => _transform.CleanupLog(text, newLine).TransformResult,
        _ => throw new ArgumentException("지원하지 않는 매크로 동작입니다.")
    };

    private static bool Matches(ReadOnlySpan<char> text, MacroStep step)
    {
        foreach (var term in step.AllTerms) if (!Contains(text, term, step)) return false;
        foreach (var term in step.ExcludeTerms) if (Contains(text, term, step)) return false;
        if (step.AnyTerms.Count == 0) return true;
        foreach (var term in step.AnyTerms) if (Contains(text, term, step)) return true;
        return false;
    }

    private static bool Contains(ReadOnlySpan<char> text, string term, MacroStep step)
    {
        var comparison = step.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!step.WholeWord) return text.Contains(term, comparison);
        var searchedThrough = 0;
        while (searchedThrough <= text.Length - term.Length)
        {
            var relativeIndex = text[searchedThrough..].IndexOf(term, comparison);
            if (relativeIndex < 0) return false;
            var start = searchedThrough + relativeIndex;
            var end = start + term.Length;
            if ((start == 0 || !IsWordCharacter(text[start - 1])) &&
                (end == text.Length || !IsWordCharacter(text[end]))) return true;
            searchedThrough = start + 1;
        }
        return false;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static void AppendLines(List<Line> output, ReadOnlyMemory<char> text, bool matched, CancellationToken cancellationToken)
    {
        var span = text.Span;
        var start = 0;
        while (start < span.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeEnd = span[start..].IndexOfAny('\r', '\n');
            if (relativeEnd < 0) break;
            var end = start + relativeEnd;
            output.Add(new Line(text.Slice(start, end - start), matched));
            start = end + (span[end] == '\r' && end + 1 < span.Length && span[end + 1] == '\n' ? 2 : 1);
        }
        output.Add(new Line(text[start..], matched));
    }

    private static string Join(List<Line> lines, int start, int count, string separator, bool terminal, CancellationToken cancellationToken)
    {
        long length = (long)Math.Max(0, count - 1) * separator.Length + (terminal ? separator.Length : 0);
        for (var i = start; i < start + count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            length += lines[i].Text.Length;
        }
        if (length > int.MaxValue) throw new ArgumentException("매크로 결과가 지원하는 텍스트 크기를 초과합니다.");
        return string.Create((int)length, (lines, start, count, separator, terminal, cancellationToken), static (destination, state) =>
        {
            var offset = 0;
            for (var i = state.start; i < state.start + state.count; i++)
            {
                state.cancellationToken.ThrowIfCancellationRequested();
                var content = state.lines[i].Text.Span;
                content.CopyTo(destination[offset..]);
                offset += content.Length;
                if (i + 1 < state.start + state.count || state.terminal)
                {
                    state.separator.AsSpan().CopyTo(destination[offset..]);
                    offset += state.separator.Length;
                }
            }
        });
    }

    private sealed class LineComparer(bool matchCase) : IEqualityComparer<ReadOnlyMemory<char>>
    {
        private readonly StringComparison _comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        public bool Equals(ReadOnlyMemory<char> left, ReadOnlyMemory<char> right) => left.Span.Equals(right.Span, _comparison);
        public int GetHashCode(ReadOnlyMemory<char> value) => string.GetHashCode(value.Span, _comparison);
    }
}

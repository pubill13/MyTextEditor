using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class TextTransformService
{
    public TextTransformResult Trim(string text, TrimOptions options, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ValidateNewLine(newLine);
        if (string.IsNullOrEmpty(options.StartMarker))
            throw new ArgumentException("기준 텍스트를 입력해야 합니다.", nameof(options));
        if (options.Operation is TrimOperation.RemoveBetween or TrimOperation.KeepBetween
            && string.IsNullOrEmpty(options.EndMarker))
            throw new ArgumentException("사이 지우기에는 종료 기준 텍스트가 필요합니다.", nameof(options));

        return TransformEachLine(text, newLine, (line, _) => TrimLine(line, options));
    }

    public TextTransformResult RemoveCharacters(string text, int count, CharacterRemovalSide side,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "제거할 글자 수는 0 이상이어야 합니다.");
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        ValidateNewLine(newLine);

        return TransformEachLine(text, newLine, (line, _) =>
        {
            var elementStarts = System.Globalization.StringInfo.ParseCombiningCharacters(line);
            if (count == 0 || elementStarts.Length == 0)
                return (line, TextChangeStatus.Unchanged);

            string result;
            if (count >= elementStarts.Length)
            {
                result = string.Empty;
            }
            else if (side == CharacterRemovalSide.Left)
            {
                result = line[elementStarts[count]..];
            }
            else
            {
                result = line[..elementStarts[elementStarts.Length - count]];
            }
            return (result, TextChangeStatus.Changed);
        });
    }

    public TextTransformResult AddPrefix(string text, string prefix, bool includeBlankLines = false,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return AddToLines(text, prefix, addAsPrefix: true, includeBlankLines, newLine);
    }

    public TextTransformResult AddSuffix(string text, string suffix, bool includeBlankLines = false,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return AddToLines(text, suffix, addAsPrefix: false, includeBlankLines, newLine);
    }

    public TextTransformResult SplitByDelimiter(string text, string delimiter, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(delimiter);
        ValidateNewLine(newLine);
        var result = string.Join(newLine, text.Split([delimiter], StringSplitOptions.None));
        return BuildWholeDocumentResult(text, result);
    }

    public TextTransformResult JoinLines(string text, string delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(delimiter);
        var result = string.Join(delimiter, TextLines.Split(text));
        return BuildWholeDocumentResult(text, result);
    }

    public TextTransformResult AddLineNumbers(string text, int startNumber = 1, string separator = ": ",
        bool includeBlankLines = false, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(separator);
        ValidateNewLine(newLine);
        var number = startNumber;
        return TransformEachLine(text, newLine, (line, _) =>
        {
            if (!includeBlankLines && string.IsNullOrWhiteSpace(line))
                return (line, TextChangeStatus.Skipped);

            var result = number.ToString(System.Globalization.CultureInfo.InvariantCulture) + separator + line;
            number++;
            return (result, TextChangeStatus.Changed);
        });
    }

    public TextTransformResult RemoveLineNumbers(string text, string separator = ": ",
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(separator);
        ValidateNewLine(newLine);
        return TransformEachLine(text, newLine, (line, _) =>
        {
            var numberEnd = 0;
            if (line.Length > 0 && line[0] == '-') numberEnd++;
            var digitStart = numberEnd;
            while (numberEnd < line.Length && line[numberEnd] is >= '0' and <= '9') numberEnd++;
            if (numberEnd == digitStart
                || !line.AsSpan(numberEnd).StartsWith(separator, StringComparison.Ordinal))
                return (line, TextChangeStatus.Skipped);

            return (line[(numberEnd + separator.Length)..], TextChangeStatus.Changed);
        });
    }

    public TextTransformResult Replace(string text, string oldValue, string newValue,
        bool matchCase = false, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        ValidateNewLine(newLine);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return TransformEachLine(text, newLine, (line, _) =>
        {
            var result = ReplaceAll(line, oldValue, newValue, comparison);
            return (result, result == line ? TextChangeStatus.Unchanged : TextChangeStatus.Changed);
        });
    }

    public TextTransformResult TrimWhitespace(string text, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateNewLine(newLine);
        return TransformEachLine(text, newLine, (line, _) =>
        {
            var result = line.Trim();
            return (result, result == line ? TextChangeStatus.Unchanged : TextChangeStatus.Changed);
        });
    }

    public TextTransformResult RemoveDuplicateLines(string text, bool matchCase = false,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateNewLine(newLine);
        var lines = TextLines.Split(text);
        var seen = new HashSet<string>(matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        return FilterLines(lines, newLine, (_, line) => seen.Add(line));
    }

    public TextTransformResult RemoveBlankLines(string text, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateNewLine(newLine);
        return FilterLines(TextLines.Split(text), newLine, (_, line) => !string.IsNullOrWhiteSpace(line));
    }

    public TextTransformResult RemoveLinesContaining(string text, string value, bool matchCase = false,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(value);
        ValidateNewLine(newLine);
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return FilterLines(TextLines.Split(text), newLine,
            (_, line) => !line.Contains(value, comparison));
    }

    public TextTransformResult CollapseBlankLines(string text, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateNewLine(newLine);
        var previousWasBlank = false;
        return FilterLines(TextLines.Split(text), newLine, (_, line) =>
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            var keep = !isBlank || !previousWasBlank;
            previousWasBlank = isBlank;
            return keep;
        });
    }

    public TextTransformResult DeleteLines(string text, IEnumerable<int> oneBasedLineNumbers,
        string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(oneBasedLineNumbers);
        ValidateNewLine(newLine);
        var selected = oneBasedLineNumbers.Where(number => number > 0).ToHashSet();
        return FilterLines(TextLines.Split(text), newLine, (index, _) => !selected.Contains(index + 1));
    }

    public TextTransformResult ExtractLines(string text, IEnumerable<int> oneBasedLineNumbers,
        int contextLines = 0, string newLine = "\r\n")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(oneBasedLineNumbers);
        ValidateNewLine(newLine);
        if (contextLines < 0) throw new ArgumentOutOfRangeException(nameof(contextLines));

        var lines = TextLines.Split(text);
        var selected = new HashSet<int>();
        foreach (var lineNumber in oneBasedLineNumbers.Where(number => number > 0))
        {
            var index = lineNumber - 1;
            for (var candidate = Math.Max(0, index - contextLines);
                 candidate <= Math.Min(lines.Count - 1, index + contextLines); candidate++)
                selected.Add(candidate);
        }

        var extracted = new List<string>();
        var preview = new List<TextChangePreview>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var include = selected.Contains(index);
            if (include) extracted.Add(lines[index]);
            preview.Add(new TextChangePreview(index + 1, lines[index], include ? lines[index] : string.Empty,
                include ? TextChangeStatus.Unchanged : TextChangeStatus.Skipped));
        }
        return BuildResult(string.Join(newLine, extracted), preview);
    }

    private static (string Result, TextChangeStatus Status) TrimLine(string line, TrimOptions options)
    {
        var comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var startIndex = line.IndexOf(options.StartMarker, comparison);
        if (startIndex < 0) return (line, TextChangeStatus.Skipped);

        string result;
        switch (options.Operation)
        {
            case TrimOperation.RemoveBefore:
                var contentStart = options.KeepStartMarker ? startIndex : startIndex + options.StartMarker.Length;
                result = line[contentStart..];
                break;
            case TrimOperation.RemoveAfter:
                var contentEnd = options.KeepStartMarker ? startIndex + options.StartMarker.Length : startIndex;
                result = line[..contentEnd];
                break;
            case TrimOperation.RemoveBetween:
            case TrimOperation.KeepBetween:
                var endMarker = options.EndMarker!;
                var endIndex = line.IndexOf(endMarker, startIndex + options.StartMarker.Length, comparison);
                if (endIndex < 0) return (line, TextChangeStatus.Skipped);
                if (options.Operation == TrimOperation.RemoveBetween)
                {
                    var prefixEnd = options.KeepStartMarker ? startIndex + options.StartMarker.Length : startIndex;
                    var suffixStart = options.KeepEndMarker ? endIndex : endIndex + endMarker.Length;
                    result = line[..prefixEnd] + line[suffixStart..];
                }
                else
                {
                    var keptStart = options.KeepStartMarker ? startIndex : startIndex + options.StartMarker.Length;
                    var keptEnd = options.KeepEndMarker ? endIndex + endMarker.Length : endIndex;
                    result = line[keptStart..keptEnd];
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }
        return (result, result == line ? TextChangeStatus.Unchanged : TextChangeStatus.Changed);
    }

    private static TextTransformResult TransformEachLine(string text, string newLine,
        Func<string, int, (string Result, TextChangeStatus Status)> transform)
    {
        var lines = TextLines.Split(text);
        var results = new string[lines.Count];
        var preview = new List<TextChangePreview>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var transformed = transform(lines[index], index);
            results[index] = transformed.Result;
            preview.Add(new TextChangePreview(index + 1, lines[index], transformed.Result, transformed.Status));
        }
        return BuildResult(string.Join(newLine, results), preview);
    }

    private static TextTransformResult AddToLines(string text, string value, bool addAsPrefix,
        bool includeBlankLines, string newLine)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateNewLine(newLine);
        return TransformEachLine(text, newLine, (line, _) =>
        {
            if (!includeBlankLines && string.IsNullOrWhiteSpace(line))
                return (line, TextChangeStatus.Skipped);
            if (value.Length == 0)
                return (line, TextChangeStatus.Unchanged);
            var result = addAsPrefix ? value + line : line + value;
            return (result, TextChangeStatus.Changed);
        });
    }

    private static TextTransformResult FilterLines(IReadOnlyList<string> lines, string newLine,
        Func<int, string, bool> shouldKeep)
    {
        var kept = new List<string>();
        var preview = new List<TextChangePreview>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var keep = shouldKeep(index, lines[index]);
            if (keep) kept.Add(lines[index]);
            preview.Add(new TextChangePreview(index + 1, lines[index], keep ? lines[index] : string.Empty,
                keep ? TextChangeStatus.Unchanged : TextChangeStatus.Changed));
        }
        return BuildResult(string.Join(newLine, kept), preview);
    }

    private static TextTransformResult BuildResult(string text, IReadOnlyList<TextChangePreview> preview)
    {
        var changed = preview.Count(item => item.Status == TextChangeStatus.Changed);
        var skipped = preview.Count(item => item.Status == TextChangeStatus.Skipped);
        var empty = preview.Count(item => item.Status == TextChangeStatus.Changed && item.ResultText.Length == 0);
        return new TextTransformResult(text, preview, new TextTransformSummary(changed, skipped, empty));
    }

    private static TextTransformResult BuildWholeDocumentResult(string original, string result)
    {
        var status = original == result ? TextChangeStatus.Unchanged : TextChangeStatus.Changed;
        return BuildResult(result, [new TextChangePreview(1, original, result, status)]);
    }

    private static string ReplaceAll(string source, string oldValue, string newValue, StringComparison comparison)
    {
        var firstIndex = source.IndexOf(oldValue, comparison);
        if (firstIndex < 0) return source;
        var builder = new System.Text.StringBuilder(source.Length);
        var copiedThrough = 0;
        for (var index = firstIndex; index >= 0; index = source.IndexOf(oldValue, copiedThrough, comparison))
        {
            builder.Append(source, copiedThrough, index - copiedThrough);
            builder.Append(newValue);
            copiedThrough = index + oldValue.Length;
        }
        builder.Append(source, copiedThrough, source.Length - copiedThrough);
        return builder.ToString();
    }

    private static void ValidateNewLine(string newLine)
    {
        if (newLine is not ("\r\n" or "\n" or "\r"))
            throw new ArgumentException("지원하지 않는 줄바꿈 형식입니다.", nameof(newLine));
    }
}

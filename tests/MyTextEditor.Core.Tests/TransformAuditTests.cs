using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class TransformAuditTests
{
    private sealed record Case(string Name, Func<TextTransformResult> Run, string Expected);
    public static Task Run()
    {
        var s = new TextTransformService();
        var cases = new List<Case>();
        void Add(string name, Func<TextTransformResult> run, string expected) => cases.Add(new(name, run, expected));
        foreach (var nl in new[] { "\r\n", "\n", "\r" })
        {
            Add("before", () => s.Trim("pre[x]post" + nl + "missing", new(TrimOperation.RemoveBefore, "["), nl), "[x]post" + nl + "missing");
            Add("after", () => s.Trim("pre[x]post" + nl + "missing", new(TrimOperation.RemoveAfter, "]"), nl), "pre[x]" + nl + "missing");
            Add("remove-between", () => s.Trim("a[x]b[y]c", new(TrimOperation.RemoveBetween, "[", "]"), nl), "a[]b[y]c");
            Add("keep-between", () => s.Trim("a[x]b[y]c", new(TrimOperation.KeepBetween, "[", "]", false, false), nl), "x");
            Add("left", () => s.RemoveCharacters("😀e\u0301가", 1, CharacterRemovalSide.Left, nl), "e\u0301가");
            Add("right", () => s.RemoveCharacters("가e\u0301👩‍💻", 2, CharacterRemovalSide.Right, nl), "가");
            Add("prefix", () => s.AddPrefix("a" + nl + " \t" + nl + "b", ">", false, nl), ">a" + nl + " \t" + nl + ">b");
            Add("suffix", () => s.AddSuffix("a" + nl + nl, "!", true, nl), "a!" + nl + "!" + nl + "!");
            Add("split", () => s.SplitByDelimiter(",a,, b,", ",", nl), nl + "a" + nl + nl + " b" + nl);
            Add("join", () => s.JoinLines("a" + nl + nl + " b" + nl, ","), "a,, b,");
            Add("numbers", () => s.AddLineNumbers("a" + nl + " " + nl + "b", 0, ": ", false, nl), "0: a" + nl + " " + nl + "1: b");
            Add("remove-numbers", () => s.RemoveLineNumbers("2026: original" + nl + "-3: x" + nl + " 1: retained" + nl + "1:no space", ": ", nl), "original" + nl + "x" + nl + " 1: retained" + nl + "1:no space");
            Add("duplicates", () => s.RemoveDuplicateLines("B" + nl + "a" + nl + "A" + nl + " a" + nl + "B", false, nl), "B" + nl + "a" + nl + " a");
            Add("blank", () => s.RemoveBlankLines(nl + "a" + nl + " \t" + nl + "b" + nl, nl), "a" + nl + "b");
            Add("collapse", () => s.CollapseBlankLines("a" + nl + " " + nl + "\t" + nl + "b", nl), "a" + nl + " " + nl + "b");
            Add("whitespace", () => s.TrimWhitespace(" \t가  나\t" + nl + "\u00a0b\u3000", nl), "가  나" + nl + "b");
            Add("containing", () => s.RemoveLinesContaining("OK" + nl + "error" + nl + "ERROR" + nl, "error", false, nl), "OK" + nl);
            Add("replace", () => s.Replace("aAa" + nl + "한글 a", "a", "b", false, nl), "bbb" + nl + "한글 b");
        }
        foreach (var keepStart in new[] { false, true })
        foreach (var keepEnd in new[] { false, true })
        {
            Add("remove-boundaries", () => s.Trim("a[x]b", new(TrimOperation.RemoveBetween, "[", "]", keepStart, keepEnd), "\n"), "a" + (keepStart ? "[" : "") + (keepEnd ? "]" : "") + "b");
            Add("keep-boundaries", () => s.Trim("a[x]b", new(TrimOperation.KeepBetween, "[", "]", keepStart, keepEnd), "\n"), (keepStart ? "[" : "") + "x" + (keepEnd ? "]" : ""));
        }
        Add("before-remove-marker", () => s.Trim("a[X]b[X]", new(TrimOperation.RemoveBefore, "[x]", KeepStartMarker: false), "\n"), "b[X]");
        Add("after-remove-marker", () => s.Trim("a[X]b[X]", new(TrimOperation.RemoveAfter, "[x]", KeepStartMarker: false), "\n"), "a");
        Add("marker-match-case", () => s.Trim("abc", new(TrimOperation.RemoveBefore, "B", MatchCase: true), "\n"), "abc");
        Add("marker-reversed", () => s.Trim("]a[", new(TrimOperation.KeepBetween, "[", "]"), "\n"), "]a[");
        Add("marker-identical", () => s.Trim("x|a|y", new(TrimOperation.KeepBetween, "|", "|", false, false), "\n"), "a");
        Add("count-zero", () => s.RemoveCharacters("가😀", 0, CharacterRemovalSide.Left, "\n"), "가😀");
        Add("count-exceeds", () => s.RemoveCharacters("가😀", int.MaxValue, CharacterRemovalSide.Right, "\n"), "");
        Add("prefix-empty", () => s.AddPrefix("a\n ", "", true, "\n"), "a\n ");
        Add("suffix-empty", () => s.AddSuffix("a", "", false, "\n"), "a");
        Add("split-multi", () => s.SplitByDelimiter("a::b::::c", "::", "\n"), "a\nb\n\nc");
        Add("split-missing", () => s.SplitByDelimiter("abc", "|", "\n"), "abc");
        Add("join-empty", () => s.JoinLines("a\r\nb\rc\n", ""), "abc");
        Add("numbers-overflow", () => s.AddLineNumbers("a\nb", int.MaxValue, ":", false, "\n"), "2147483647:a\n2147483648:b");
        Add("numbers-max-single", () => s.AddLineNumbers("a", int.MaxValue, ":", false, "\n"), "2147483647:a");
        Add("dedup-sensitive", () => s.RemoveDuplicateLines("a\nA\na", true, "\n"), "a\nA");
        Add("containing-sensitive", () => s.RemoveLinesContaining("Error\nerror", "error", true, "\n"), "Error");
        Add("containing-all", () => s.RemoveLinesContaining("x\nx\n", "x", false, "\n"), "");
        Add("replace-sensitive", () => s.Replace("AaA", "a", "", true, "\n"), "AA");
        Add("replace-overlap", () => s.Replace("aaaaa", "aa", "x", false, "\n"), "xxa");
        Add("replace-same", () => s.Replace("abc", "b", "b", false, "\n"), "abc");
        Add("mixed-eol", () => s.TrimWhitespace("a\r\nb\rc\n", "\n"), "a\nb\nc\n");
        foreach (var operation in Enum.GetValues<TrimOperation>())
            Add("empty-trim", () => s.Trim("", new(operation, "[", "]"), "\n"), "");
        Add("empty-blank", () => s.RemoveBlankLines("", "\n"), "");
        Add("empty-collapse", () => s.CollapseBlankLines("", "\n"), "");
        Add("empty-duplicate", () => s.RemoveDuplicateLines("", false, "\n"), "");
        Add("empty-replace", () => s.Replace("", "x", "y", false, "\n"), "");
        Add("empty-split", () => s.SplitByDelimiter("", ",", "\n"), "");
        Add("empty-join", () => s.JoinLines("", ","), "");
        Add("empty-number", () => s.AddLineNumbers("", 1, ": ", false, "\n"), "");
        Add("empty-number-remove", () => s.RemoveLineNumbers("", ": ", "\n"), "");
        Add("empty-prefix", () => s.AddPrefix("", "x", false, "\n"), "");
        Add("empty-suffix", () => s.AddSuffix("", "x", false, "\n"), "");
        Add("empty-characters", () => s.RemoveCharacters("", 1, CharacterRemovalSide.Left, "\n"), "");
        Add("empty-whitespace", () => s.TrimWhitespace("", "\n"), "");
        Add("empty-containing", () => s.RemoveLinesContaining("", "x", false, "\n"), "");
        var failures = new List<string>();
        for (var round = 0; round < 3; round++)
        foreach (var c in cases)
        {
            try
            {
                var result = c.Run();
                if (result.Text != c.Expected) throw new Exception($"expected [{c.Expected}] actual [{result.Text}]");
                var summary = new TextTransformSummary(result.Preview.Count(x => x.Status == TextChangeStatus.Changed), result.Preview.Count(x => x.Status == TextChangeStatus.Skipped), result.Preview.Count(x => x.Status == TextChangeStatus.Changed && x.ResultText.Length == 0));
                if (summary != result.Summary) throw new Exception("Preview and summary disagree");
            }
            catch (Exception e) { failures.Add($"round {round + 1} {c.Name}: {e.Message}"); }
        }
        Action[] invalid = [
            () => s.Trim("abc", new(TrimOperation.RemoveBefore, "")),
            () => s.Trim("abc", new(TrimOperation.KeepBetween, "[", "")),
            () => s.Trim("abc", new((TrimOperation)99, "absent")),
            () => s.RemoveCharacters("abc", -1, CharacterRemovalSide.Left),
            () => s.RemoveCharacters("abc", 1, (CharacterRemovalSide)99),
            () => s.SplitByDelimiter("abc", ""),
            () => s.AddLineNumbers("abc", separator: ""),
            () => s.RemoveLineNumbers("abc", ""),
            () => s.Replace("abc", "", "x"),
            () => s.RemoveLinesContaining("abc", ""),
            () => s.TrimWhitespace("abc", "invalid")
        ];
        for (var round = 0; round < 3; round++)
        for (var i = 0; i < invalid.Length; i++)
        {
            try { invalid[i](); failures.Add($"invalid input {i} accepted"); }
            catch (ArgumentException) { }
        }
        Console.WriteLine($"Transform audit: {cases.Count} output cases + {invalid.Length} invalid inputs, each repeated 3 times");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
        return Task.CompletedTask;
    }
}

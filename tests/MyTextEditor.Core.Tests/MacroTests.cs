using System.Text.Json;
using MyTextEditor.Core.Macros;

internal static class MacroTests
{
    private static MacroStep Search(string term, MacroTarget target = MacroTarget.WholeDocument) =>
        new() { Operation = MacroOperation.Search, AllTerms = [term], Target = target };
    private static MacroStep Step(MacroOperation operation, string value = "", MacroTarget target = MacroTarget.WholeDocument) =>
        new() { Operation = operation, Value = value, Target = target };
    private static MacroDefinition Macro(params MacroStep[] steps) => new() { Name = "회귀 테스트", Steps = steps.ToList() };
    private static MacroExecutionResult Run(string text, params MacroStep[] steps) => new TextMacroRunner().Run(text, "\n", Macro(steps));

    public static Task SequenceAndTracking()
    {
        var replace = Step(MacroOperation.Replace, "AAA"); replace.Replacement = "renamed";
        var result = Run(" AAA one \nBBB two\nAAA three", Step(MacroOperation.TrimWhitespace), Search("AAA"),
            replace, Step(MacroOperation.AddPrefix, "X:", MacroTarget.MatchedLines));
        Assert.Equal("X:renamed one\nBBB two\nX:renamed three", result.Text);
        Assert.Equal(2, result.Steps[1].MatchedLines);
        Assert.Equal(2, result.Steps[3].ChangedLines);
        var disabled = Step(MacroOperation.DeleteTargetLines); disabled.Enabled = false;
        Assert.Equal("P:a\nP:b", Run("a\nb", disabled, Step(MacroOperation.AddPrefix, "P:")).Text);
        return Task.CompletedTask;
    }

    public static Task SplitJoinTracking()
    {
        var selected = MacroTarget.MatchedLines;
        Assert.Equal("1:AAA\n2:b\nmiddle\n3:AAA\n4:c", Run("AAA|b\nmiddle\nAAA|c", Search("AAA"),
            Step(MacroOperation.SplitByDelimiter, "|", selected), Step(MacroOperation.AddLineNumbers, ":", selected)).Text);
        Assert.Equal("AAA|b\nmiddle\nAAA|c", Run("AAA|b\nmiddle\nAAA|c", Search("AAA"),
            Step(MacroOperation.SplitByDelimiter, "|", selected), Step(MacroOperation.JoinLines, "|", selected)).Text);
        Assert.Equal("a;b;", Run("a\nb\n", Step(MacroOperation.JoinLines, ";")).Text);
        Assert.Equal("a", Run("a\n", Step(MacroOperation.JoinLines)).Text);
        Assert.Equal("", Run("\n", Step(MacroOperation.JoinLines)).Text);
        Assert.Equal("a\n\nb", Run("a||b", Step(MacroOperation.SplitByDelimiter, "|")).Text);
        Assert.Equal("AAA!\nb!\n", Run("AAA|b\n", Search("AAA"), Step(MacroOperation.SplitByDelimiter, "|", selected),
            Step(MacroOperation.AddSuffix, "!", selected)).Text);
        return Task.CompletedTask;
    }

    public static Task DeleteDedupAndNumbers()
    {
        var selected = MacroTarget.MatchedLines;
        Assert.Equal("AAA\nkeep", Run("AAA\nkeep\nAAA", Search("AAA"), Step(MacroOperation.RemoveDuplicateLines, target: selected)).Text);
        Assert.Equal("keep\n", Run("AAA\nkeep\nAAA\n", Search("AAA"), Step(MacroOperation.DeleteTargetLines, target: selected)).Text);
        Assert.Equal("", Run("AAA\nAAA\n", Search("AAA"), Step(MacroOperation.DeleteTargetLines, target: selected)).Text);
        Assert.Equal("AAA\nAAA\n", Run("AAA\nkeep\nAAA\n", Search("AAA"), Step(MacroOperation.KeepTargetLines, target: selected)).Text);
        var number = Step(MacroOperation.AddLineNumbers, ": ", selected); number.StartNumber = 5;
        Assert.Equal("5: AAA\nkeep\n6: AAA", Run("AAA\nkeep\nAAA", Search("AAA"), number).Text);
        return Task.CompletedTask;
    }

    public static Task EmptyResultsAndSearch()
    {
        Assert.Equal("P:a", Run("a", Search("missing"), Step(MacroOperation.KeepTargetLines, target: MacroTarget.MatchedLines),
            Step(MacroOperation.AddPrefix, "P:")).Text);
        var replace = Step(MacroOperation.Replace, "AAA"); replace.Replacement = "BBB";
        Assert.Equal("BBB", Run("AAA", Search("AAA"), replace, Search("AAA"), Step(MacroOperation.DeleteTargetLines, target: MacroTarget.MatchedLines)).Text);
        var search = new MacroStep { Operation = MacroOperation.Search, AllTerms = ["AAA"], AnyTerms = ["BBB", "DDD"], ExcludeTerms = ["CC"] };
        Assert.Equal("aaa bbb", Run("aaa bbb\nAAA BBB CC\nAAA\nAAA DDD CC", search, Step(MacroOperation.KeepTargetLines, target: MacroTarget.MatchedLines)).Text);
        Assert.Equal("AAA BBB", Run("AAA BBB\nAAA\nBBB", Search("AAA"), Search("BBB", MacroTarget.MatchedLines),
            Step(MacroOperation.KeepTargetLines, target: MacroTarget.MatchedLines)).Text);
        return Task.CompletedTask;
    }

    public static Task UnicodeAndNewlines()
    {
        var remove = Step(MacroOperation.RemoveCharactersLeft); remove.Count = 1;
        Assert.Equal("한글\nx", Run("👩‍💻한글\néx", remove).Text);
        foreach (var newline in new[] { "\n", "\r\n", "\r" })
        {
            var runner = new TextMacroRunner();
            foreach (var terminal in new[] { "", newline })
            {
                var result = runner.Run("AAA" + newline + "keep" + terminal, newline,
                    Macro(Step(MacroOperation.RemoveLinesContaining, "AAA")));
                Assert.Equal("keep" + terminal, result.Text);
            }
        }
        Assert.Equal("  INFO 한글\n\nnext", Run("\u001b[31m  INFO 한글\u001b[0m  \n\n\nnext\0", Step(MacroOperation.CleanupLog)).Text);
        var between = Step(MacroOperation.KeepBetween, "["); between.EndMarker = "]"; between.KeepStart = false; between.KeepEnd = false;
        Assert.Equal("한글\nmissing", Run("a[한글]b\nmissing", between).Text);
        return Task.CompletedTask;
    }

    public static Task ValidationAndCancellation()
    {
        var runner = new TextMacroRunner();
        Assert.True(runner.Validate(Macro(Step(MacroOperation.AddPrefix, "!", MacroTarget.MatchedLines))).Count > 0);
        Assert.True(runner.Validate(Macro(Step(MacroOperation.Replace))).Count > 0);
        Assert.True(runner.Validate(Macro(new MacroStep { Operation = (MacroOperation)900 })).Count > 0);
        var definition = Macro(Step(MacroOperation.AddPrefix, "a"), Step(MacroOperation.AddSuffix, "b"));
        Assert.Equal("ax", runner.Run("x", "\n", definition, stopAfterStep: 0).Text);
        using var source = new CancellationTokenSource();
        var progress = new InlineProgress(_ => source.Cancel());
        Expect<OperationCanceledException>(() => runner.Run("x", "\n", definition, source.Token, progress));
        var skipped = Step(MacroOperation.RemoveBefore, "missing");
        Assert.Equal(1, runner.Run("x", "\n", Macro(skipped)).Steps[0].SkippedLines);
        var mixed = "a\r\nb\nc\r";
        Assert.Equal(mixed, runner.Run(mixed, "\n", Macro(Search("a"))).Text);
        Assert.Equal(mixed, runner.Run(mixed, "\n", definition, stopAfterStep: -1).Text);
        var noChange = Step(MacroOperation.Replace, "missing"); noChange.Replacement = "new";
        Assert.Equal(mixed, runner.Run(mixed, "\n", Macro(noChange)).Text);
        var emptyPrefix = Step(MacroOperation.AddPrefix, "new"); emptyPrefix.IncludeBlankLines = true;
        Assert.Equal("new", Run("old\n", Step(MacroOperation.DeleteTargetLines), emptyPrefix).Text);
        var lastNumber = Step(MacroOperation.AddLineNumbers, ":"); lastNumber.StartNumber = int.MaxValue;
        Assert.Equal("2147483647:x", Run("x", lastNumber).Text);
        Expect<ArgumentException>(() => Run("x\ny", lastNumber));
        return Task.CompletedTask;
    }

    public static Task JsonRoundtrip()
    {
        var definition = Macro(Search("한글"), Step(MacroOperation.AddPrefix, "hello", MacroTarget.MatchedLines));
        definition.Steps[0].WholeWord = true;
        var json = MacroJsonCodec.Serialize(definition);
        var copy = MacroJsonCodec.Deserialize(json);
        Assert.Equal(definition.Id, copy.Id);
        Assert.Equal("한글", copy.Steps[0].AllTerms[0]);
        Assert.True(copy.Steps[0].WholeWord);
        Assert.Equal(MacroTarget.MatchedLines, copy.Steps[1].Target);
        Expect<JsonException>(() => MacroJsonCodec.Deserialize(json.Replace("\"Version\": 1", "\"Version\": 9")));
        Expect<JsonException>(() => MacroJsonCodec.Deserialize(json.Replace("\"Search\"", "\"Unknown\"")));
        Expect<JsonException>(() => MacroJsonCodec.Deserialize(json.Replace("\"Search\"", "900")));
        Expect<JsonException>(() => MacroJsonCodec.Deserialize("{bad json}"));
        var invalid = Macro(Search("ok")); invalid.Steps[0].Enabled = false; invalid.Steps[0].AllTerms = [null!];
        Expect<JsonException>(() => MacroJsonCodec.Deserialize(MacroJsonCodec.Serialize(invalid)));
        return Task.CompletedTask;
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private sealed class InlineProgress(Action<MacroStepResult> callback) : IProgress<MacroStepResult>
    {
        public void Report(MacroStepResult value) => callback(value);
    }
}

using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class TransformAllocationTests
{
    public static Task Run()
    {
        var service = new TextTransformService();
        var samples = new[] { "", "\n", " \r\n\t\r\n", " a \rb\nA\r\n", "delete\nkeep\n", "keep\n\ndelete", "Aa\r\naA\n" };
        foreach (var text in samples)
        foreach (var newline in new[] { "\n", "\r\n", "\r" })
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Check(service.TrimWhitespace(text, newline), lines,
                lines.Select(line => line.Trim()).ToArray(), null, newline);
            Check(service.Replace(text, "a", "x\ny", false, newline), lines,
                lines.Select(line => line.Replace("a", "x\ny", StringComparison.OrdinalIgnoreCase)).ToArray(), null, newline);
            Check(service.RemoveBlankLines(text, newline), lines, lines,
                lines.Select(line => !string.IsNullOrWhiteSpace(line)).ToArray(), newline);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Check(service.RemoveDuplicateLines(text, false, newline), lines, lines,
                lines.Select(seen.Add).ToArray(), newline);
            var terminal = text.EndsWith('\n') || text.EndsWith('\r');
            var content = terminal ? lines[..^1] : lines;
            Check(service.RemoveLinesContaining(text, "delete", false, newline), content, content,
                content.Select(line => !line.Contains("delete", StringComparison.OrdinalIgnoreCase)).ToArray(), newline, terminal);
        }
        var statusPreview = (ITextChangePreviewList)service.TrimWhitespace(" a ").Preview;
        if (statusPreview.GetStatus(0) != TextChangeStatus.Changed)
            throw new Exception("Range status lookup differs from preview status.");
        var numbered = service.AddLineNumbers("a\n \nb\n", 4, ": ", false, "\n");
        if (numbered.Text != "4: a\n \n5: b\n" || numbered.Preview[2].ResultText != "5: b"
            || numbered.Preview[0].ResultText != "4: a" || numbered.Summary.SkippedLines != 2)
            throw new Exception("Lazy preview must preserve one-time stateful transformations.");

        var large = string.Join('\n', Enumerable.Repeat(new string('x', 120), 100_000));
        service.TrimWhitespace(" warmup ");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = service.TrimWhitespace(large, "\n");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (result.Text != large || result.Preview.Count != 100_000 || result.Preview[^1].OriginalText.Length != 120)
            throw new Exception("Large transformation or lazy preview is incomplete.");
        if (allocated > large.Length * 5L)
            throw new Exception($"Range transformation allocated {allocated:N0} bytes for {large.Length:N0} characters.");
        Console.WriteLine($"  100,000 line cleanup: {allocated:N0} allocated bytes");
        return Task.CompletedTask;
    }

    private static void Check(TextTransformResult actual, string[] original, string[] transformed,
        bool[]? keep, string newline, bool terminal = false)
    {
        var expected = new List<TextChangePreview>();
        var output = new List<string>();
        for (var i = 0; i < original.Length; i++)
        {
            var kept = keep is null || keep[i];
            var after = kept ? transformed[i] : string.Empty;
            if (kept) output.Add(after);
            var status = !kept || after != original[i] ? TextChangeStatus.Changed : TextChangeStatus.Unchanged;
            expected.Add(new TextChangePreview(i + 1, original[i], after, status));
        }
        var expectedText = string.Join(newline, output) + (terminal && output.Count > 0 ? newline : "");
        var summary = new TextTransformSummary(expected.Count(x => x.Status == TextChangeStatus.Changed), 0,
            expected.Count(x => x.Status == TextChangeStatus.Changed && x.ResultText.Length == 0));
        if (actual.Text != expectedText || !actual.Preview.SequenceEqual(expected) || actual.Summary != summary)
            throw new Exception("Range transform changed output, preview, or summary semantics.");
    }
}

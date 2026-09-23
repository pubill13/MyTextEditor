using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class DiffOptimizationTests
{
    public static Task Run()
    {
        var engine = new TextDiffEngine();
        var random = new Random(47);
        for (var sample = 0; sample < 250; sample++)
        {
            var a = Enumerable.Range(0, random.Next(1, 30)).Select(_ => ((char)('a' + random.Next(8))).ToString()).ToArray();
            var b = Enumerable.Range(0, random.Next(1, 30)).Select(_ => ((char)('c' + random.Next(8))).ToString()).ToArray();
            var left = string.Join('\n', a);
            var right = string.Join('\n', b);
            var result = engine.Compare(left, right);
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    lcs[i, j] = a[i - 1] == b[j - 1] ? lcs[i - 1, j - 1] + 1 : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            var changed = result.Blocks.Sum(block => block.LeftLineCount + block.RightLineCount);
            if (changed != a.Length + b.Length - 2 * lcs[a.Length, b.Length])
                throw new Exception("Pruned diff is not a shortest edit script.");
            var mergedRight = right;
            foreach (var block in result.Blocks.Reverse())
                mergedRight = new TextMergeService().ApplyBlock(left, mergedRight, block, DiffSide.Left, "\n", "\n").RightText;
            if (mergedRight != left) throw new Exception("Original merge mapping lost text.");
            for (var i = 1; i < result.ScrollAnchors.Count; i++)
                if (result.ScrollAnchors[i].LeftLineNumber < result.ScrollAnchors[i - 1].LeftLineNumber ||
                    result.ScrollAnchors[i].RightLineNumber < result.ScrollAnchors[i - 1].RightLineNumber)
                    throw new Exception("Non-monotonic scroll anchors.");
        }
        var oldText = string.Join('\n', Enumerable.Range(0, 50_000).Select(i => $"old {i} alpha"));
        var newText = string.Join('\n', Enumerable.Range(0, 50_000).Select(i => $"new {i} beta"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var large = engine.Compare(oldText, newText);
        if (large.ModifiedLines != 50_000 || large.Blocks.Count != 1) throw new Exception("Large replacement incorrect.");
        if (large.ScrollAnchors[^1] != new DiffLineAnchor(50_001, 50_001)) throw new Exception("Missing end anchor.");
        Console.WriteLine($"  50,000 entirely changed lines: {watch.Elapsed.TotalMilliseconds:F0}ms");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { engine.Compare(oldText, newText, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { return Task.CompletedTask; }
        throw new Exception("Canceled comparison completed.");
    }
}

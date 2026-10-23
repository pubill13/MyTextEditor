using MyTextEditor.Core;
using MyTextEditor.Core.Models;

static class RangeDiffTests
{
    public static Task Run()
    {
        var engine = new TextDiffEngine();
        var random = new Random(45);
        for (var sample = 0; sample < 200; sample++)
        {
            string Make() => string.Join('\n', Enumerable.Range(0, random.Next(1, 40))
                .Select(_ => random.Next(4) == 0 ? " \t" : "line" + random.Next(10)));
            var a = Make(); var b = Make();
            var options = new DiffOptions(sample % 2 == 0, sample % 3 == 0, sample % 4 == 0);
            var expected = engine.Compare(a, b, options);
            var actual = engine.CompareRanges(a, b, options);
            if (expected.Blocks.Count != actual.Blocks.Count) throw new Exception("Block count mismatch");
            for (var i = 0; i < expected.Blocks.Count; i++)
            {
                var x = expected.Blocks[i]; var y = actual.Blocks[i];
                if ((x.Kind, x.LeftStartLine, x.RightStartLine, x.LeftLineCount, x.RightLineCount) !=
                    (y.Kind, y.LeftStartLine, y.RightStartLine, y.LeftLineCount, y.RightLineCount)) throw new Exception("Range mismatch");
            }
        }
        if (engine.CompareRanges("\0", "").Blocks.Count == 0) throw new Exception("NUL lost");
        if (engine.CompareRanges(" A  b\t", "a b", new(true, true)).HasDifferences) throw new Exception("Normalization mismatch");
        if (engine.CompareRanges("\U00010400", "\U00010428", new(IgnoreCase: true)).HasDifferences)
            throw new Exception("Supplementary Unicode case mismatch");
        var mapped = engine.CompareRanges("same\nold\n \nold2\nend", "same\nnew\n\n\nnew2\nend", new(IgnoreEmptyLines: true));
        var mappedBlock = mapped.Blocks.Single();
        if (TextDiffEngine.GetLineNumber(mappedBlock, 1, DiffSide.Left) != 4 ||
            TextDiffEngine.GetLineNumber(mappedBlock, 1, DiffSide.Right) != 5)
            throw new Exception("Ignored empty line source mapping lost");
        var longResult = engine.CompareRanges(new string('a', 1000000), new string('b', 1000000));
        var inline = engine.ComputeInlineChanges(longResult.Blocks[0].Lines[0]);
        if (inline.LeftChanges.Single().Length != 1000000) throw new Exception("Long line mismatch");
        var prefix = new string('a', 20000);
        var emojiPair = engine.ComputeInlineChanges(new(1, prefix + "😀", [], 1, prefix + "😃", []));
        if (emojiPair.LeftChanges.Single() != new InlineDiffSpan(20000, 2))
            throw new Exception("Long inline span splits surrogate pair");
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            if (TextDiffEngine.GetLineNumber(longResult.Blocks[0], 0, DiffSide.Left) != 1 ||
                TextDiffEngine.GetLineLength(longResult.Blocks[0], 0, DiffSide.Left) != 1000000)
                throw new Exception("Range metadata mismatch");
        if (GC.GetAllocatedBytesForCurrentThread() - allocated > 1024)
            throw new Exception("Metadata query materialized line text");
        var repeatedLeft = string.Join('\n', Enumerable.Range(0, 6000).Select(i => "repeat" + i % 13));
        var repeatedRight = "inserted\n" + repeatedLeft.Replace("repeat7\nrepeat8", "changed", StringComparison.Ordinal);
        var repeatedResult = engine.CompareRanges(repeatedLeft, repeatedRight);
        var merge = new TextMergeService();
        // Reverse block application preserves original offsets, including coarse large-input alignment.
        foreach (var block in repeatedResult.Blocks.Reverse())
            repeatedRight = merge.ApplyBlock(repeatedLeft, repeatedRight, block, DiffSide.Left).RightText;
        if (repeatedRight != repeatedLeft) throw new Exception("Large repeated range merge lost source content");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { engine.CompareRanges("a", "b", cancellationToken: cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        return Task.CompletedTask;
    }
}

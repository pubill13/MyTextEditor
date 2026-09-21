using System.Text;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class SearchWorkflowTests
{
    public static Task Occurrences()
    {
        const string text = "한글😀 cat CAT catapult cat";
        Assert.Equal(5, TextOccurrenceSearch.Find(text, "cat", 0)!.Start);
        Assert.Equal(9, TextOccurrenceSearch.Find(text, "cat", 8)!.Start);
        Assert.Equal(22, TextOccurrenceSearch.Find(text, "cat", 12, new(WholeWord: true))!.Start);
        Assert.True(TextOccurrenceSearch.Find(text, "cat", text.Length)!.Wrapped);
        Assert.Equal(22, TextOccurrenceSearch.Find(text, "cat", 0, backwards: true)!.Start);
        Assert.Equal(9, TextOccurrenceSearch.Find(text, "cat", 13, backwards: true)!.Start);
        Assert.Equal(5, TextOccurrenceSearch.Find(text, "cat", 8, backwards: true)!.Start);
        Assert.True(TextOccurrenceSearch.Find(text, "cat", 0, backwards: true, wrap: false) is null);
        Assert.True(TextOccurrenceSearch.Find(text, "", 0) is null);
        Assert.True(TextOccurrenceSearch.Find("😀", "\uDE00", 0) is null);
        Assert.True(TextOccurrenceSearch.Find("a\u0301 a", "a", 0, new(WholeWord: true))!.Start == 3);
        Assert.True(TextOccurrenceSearch.Find("𐐀cat", "cat", 0, new(WholeWord: true)) is null);
        Assert.True(TextOccurrenceSearch.Find("CAT", "cat", 0, new(MatchCase: true)) is null);
        Assert.Equal(0, TextOccurrenceSearch.Find("aaa", "aa", 1, backwards: true)!.Start);
        return Task.CompletedTask;
    }

    public static async Task FolderSearch()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Path.Combine(Path.GetTempPath(), "OmniEdit-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "하위 폴더"));
        try
        {
            var utf8 = Path.Combine(root, "한글 로그.LOG");
            const string content = "before\r\n한글 AAA 😀\r\nafter\r\n";
            await File.WriteAllTextAsync(utf8, content, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "하위 폴더", "한국어.txt"), "한글 AAA\n", Encoding.GetEncoding(949));
            await File.WriteAllTextAsync(Path.Combine(root, "utf16.txt"), "한글 AAA\r", Encoding.BigEndianUnicode);
            await File.WriteAllTextAsync(Path.Combine(root, "skip.bin"), "AAA");
            await File.WriteAllBytesAsync(Path.Combine(root, "bad.txt"), [0xEF, 0xBB, 0xBF, 0xFF]);
            var condition = new TextCondition(TextConditionKind.Contains, "AAA");
            var service = new FolderTextSearchService();
            var result = await service.SearchAsync(root, condition, new(ContextLines: 1));
            Assert.Equal(3, result.Files.Count);
            Assert.Equal(1, result.Failures.Count);
            var file = result.Files.Single(f => f.FilePath == utf8);
            Assert.Equal("\r\n", file.NewLine);
            Assert.Equal(2, file.Matches[0].LineNumber);
            Assert.Equal("한글 AAA 😀", file.Matches[0].Text);
            Assert.Equal(3, file.Matches[0].Context.Count);
            Assert.Equal(new FileInfo(utf8).Length, file.Length);
            Assert.Equal(new FileInfo(utf8).LastWriteTimeUtc, file.LastWriteUtc);
            Assert.Equal(content, await File.ReadAllTextAsync(utf8));
            var top = await service.SearchAsync(root, condition, folderOptions: new(false, "*.LOG"));
            Assert.Equal(1, top.Files.Count);
            Assert.Equal(0, top.Failures.Count);
            using (var locked = new FileStream(utf8, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var inaccessible = await service.SearchAsync(root, condition);
                Assert.Equal(2, inaccessible.Files.Count);
                Assert.True(inaccessible.Failures.Any(f => f.FilePath == utf8));
            }
            var missing = await service.SearchAsync(Path.Combine(root, "missing"), condition);
            Assert.Equal(1, missing.Failures.Count);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(root, condition, cancellationToken: canceled.Token));
            Assert.Throws<ArgumentException>(() => service.SearchAsync(root, condition, folderOptions: new(FilePatterns: "../*.txt")));
            Assert.Throws<OperationCanceledException>(() => new TextSearchEngine().Search(content, condition, cancellationToken: canceled.Token));
            using var cancelDuring = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(root, condition,
                progress: new CancelProgress(cancelDuring), cancellationToken: cancelDuring.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<FolderSearchProgress>
    {
        public void Report(FolderSearchProgress value) => cancellation.Cancel();
    }
}

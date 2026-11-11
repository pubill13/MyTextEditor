using MyTextEditor.Core;

internal static class FolderCompareTests
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniEdit-FolderCompare-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            void Write(string folder, string name, string text)
            {
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            Write(left, "equal.txt", "same"); Write(right, "equal.txt", "same");
            File.SetLastWriteTimeUtc(Path.Combine(right, "equal.txt"), DateTime.UtcNow.AddDays(-1));
            Write(left, "changed.txt", "AAA"); Write(right, "changed.txt", "BBB");
            Write(left, "size.txt", "A"); Write(right, "size.txt", "BBBB");
            Write(left, "empty", ""); Write(right, "empty", "");
            Write(left, "sub/left.txt", "left"); Write(right, "sub/right.txt", "right");
            Write(left, "Case.txt", "case"); Write(right, "case.txt", "case");
            Write(left, "conflict", "file"); Write(right, "conflict/child.txt", "child");
            File.WriteAllBytes(Path.Combine(left, "binary"), [0, 1, 2, 255]);
            File.WriteAllBytes(Path.Combine(right, "binary"), [0, 1, 3, 255]);
            var large = new byte[200000];
            File.WriteAllBytes(Path.Combine(left, "large"), large);
            large[^1] = 1;
            File.WriteAllBytes(Path.Combine(right, "large"), large);
            Write(left, ".hidden", "hidden");
            File.SetAttributes(Path.Combine(left, ".hidden"), FileAttributes.Hidden);
            var service = new FolderCompareService();
            var results = await service.CompareAsync(left, right);
            FolderCompareEntry Find(string name) => results.Single(e => e.RelativePath.Equals(name.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
            Check(Find("equal.txt").Status == FolderCompareStatus.Equal, "Timestamps must not affect equality");
            Check(Find("changed.txt").Status == FolderCompareStatus.Different, "Same size different bytes");
            Check(Find("size.txt").Status == FolderCompareStatus.Different, "Different length");
            Check(Find("empty").Status == FolderCompareStatus.Equal, "Empty files");
            Check(Find("sub/left.txt").Status == FolderCompareStatus.LeftOnly, "Recursive left-only file");
            Check(Find("sub/right.txt").Status == FolderCompareStatus.RightOnly, "Recursive right-only file");
            Check(Find("Case.txt").Status == FolderCompareStatus.Equal, "Windows case insensitive pairing");
            Check(Find("binary").Status == FolderCompareStatus.Different, "Binary comparison");
            Check(Find("large").Status == FolderCompareStatus.Different, "Compare beyond first stream buffer");
            Check(Find(".hidden").Status == FolderCompareStatus.LeftOnly, "Hidden files included");
            Check(Find("conflict").Status == FolderCompareStatus.Error && Find("conflict/child.txt").Status == FolderCompareStatus.Error, "File/directory conflicts must not become one-sided files");
            Check(results.Select(e => e.RelativePath).SequenceEqual(results.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)), "Stable relative path ordering");
            using (var locked = new FileStream(Path.Combine(left, "equal.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = await service.CompareFileAsync("equal.txt", Path.Combine(left, "equal.txt"), Path.Combine(right, "equal.txt"));
                Check(failed.Status == FolderCompareStatus.Error && !string.IsNullOrWhiteSpace(failed.Error), "Unreadable file must retain error");
            }
            var invalid = await service.CompareAsync(Path.Combine(root, "missing"), right);
            Check(invalid.All(e => e.Status == FolderCompareStatus.Error), "Unknown subtree must not be reported as right-only");
            Write(right, "sub/left.txt", "left");
            var refreshed = await service.CompareFileAsync("sub/left.txt", Path.Combine(left, "sub/left.txt"), Path.Combine(right, "sub/left.txt"));
            Check(refreshed.Status == FolderCompareStatus.Equal, "Saved counterpart refresh");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await service.CompareAsync(left, right, cancellationToken: cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

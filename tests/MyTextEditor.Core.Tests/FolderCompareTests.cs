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
            Write(left, "sub/deep/grandchild.txt", "before"); Write(right, "sub/deep/grandchild.txt", "after");
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
            Check(Find("sub").IsDirectory && Find("sub").Status == FolderCompareStatus.NotCompared, "Child folders must remain uninspected until opened");
            Check(results.All(entry => !entry.RelativePath.Contains(Path.DirectorySeparatorChar)), "Initial comparison scanned child contents");
            var child = await service.CompareAsync(Path.Combine(left, "sub"), Path.Combine(right, "sub"));
            Check(child.Single(entry => entry.RelativePath == "left.txt").Status == FolderCompareStatus.LeftOnly, "Opening child folder must compare immediate left file");
            Check(child.Single(entry => entry.RelativePath == "right.txt").Status == FolderCompareStatus.RightOnly, "Opening child folder must compare immediate right file");
            Check(child.Single(entry => entry.RelativePath == "deep").Status == FolderCompareStatus.NotCompared && child.All(entry => !entry.RelativePath.Contains(Path.DirectorySeparatorChar)), "Nested folder must also stay lazy");
            var grandchild = await service.CompareAsync(Path.Combine(left, "sub/deep"), Path.Combine(right, "sub/deep"));
            Check(grandchild.Single().Status == FolderCompareStatus.Different, "Grandchild file must compare when its folder is opened");
            Check(Find("Case.txt").Status == FolderCompareStatus.Equal, "Windows case insensitive pairing");
            Check(Find("binary").Status == FolderCompareStatus.Different, "Binary comparison");
            Check(Find("large").Status == FolderCompareStatus.Different, "Compare beyond first stream buffer");
            Check(Find(".hidden").Status == FolderCompareStatus.LeftOnly, "Hidden files included");
            Check(Find("conflict").Status == FolderCompareStatus.Error, "File/directory conflict must be an error");
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
            var onlyChild = await service.CompareAsync(Path.Combine(left, "sub"), Path.Combine(right, "absent"), allowMissingFolders: true);
            Check(onlyChild.All(entry => entry.Status == FolderCompareStatus.LeftOnly) && !Directory.Exists(Path.Combine(right, "absent")), "Opening one-sided folder must use a virtual empty counterpart without disk writes");
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            for (var i = 0; i < 1000; i++)
                Check((await service.CompareFileAsync("equal.txt", Path.Combine(left, "equal.txt"), Path.Combine(right, "equal.txt"))).Status == FolderCompareStatus.Equal, "Pooled comparison changed equality");
            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Check(allocated < 32 * 1024 * 1024, $"Small file comparisons allocated {allocated:N0} bytes; buffers should be reused");
            Console.WriteLine($"  1,000 nonempty file comparisons: {allocated:N0} allocated bytes");
            var big = Path.Combine(left, "big.bin");
            using (var stream = File.Create(big)) stream.SetLength(256L * 1024 * 1024);
            allocatedBefore = GC.GetTotalAllocatedBytes(true);
            Check((await service.CompareFileAsync("big.bin", big, big)).Status == FolderCompareStatus.Equal, "256MiB streaming comparison");
            allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            Check(allocated < 8 * 1024 * 1024, "Large file comparison loaded file contents into memory");
            using (var duringRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(1)))
            {
                try { await service.CompareFileAsync("big.bin", big, big, duringRead.Token); throw new InvalidOperationException("Large read ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try { await service.CompareAsync(left, right, cancellationToken: cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

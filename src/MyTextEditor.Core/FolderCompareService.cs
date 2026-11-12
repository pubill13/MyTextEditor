using System.Buffers;

namespace MyTextEditor.Core;

public enum FolderCompareStatus { Equal, Different, LeftOnly, RightOnly, Error, NotCompared }

public sealed record FolderCompareEntry(string RelativePath, string LeftPath, string RightPath,
    FolderCompareStatus Status, long? LeftLength = null, long? RightLength = null, string? Error = null,
    bool IsDirectory = false, DateTime? LeftWrittenUtc = null, DateTime? RightWrittenUtc = null);

public enum FolderComparePhase { ScanningLeft, ScanningRight, Comparing }
public sealed record FolderCompareProgress(string RelativePath, int ComparedFiles,
    FolderComparePhase Phase = FolderComparePhase.Comparing, int? TotalEntries = null,
    int DiscoveredEntries = 0, long BytesRead = 0, long FileBytes = 0);

public sealed class FolderCompareService
{
    private sealed record Item(string Path, long? Length, DateTime Written);
    private sealed record Inventory(Dictionary<string, Item> Files, Dictionary<string, Item> Directories,
        Dictionary<string, string> Failures);

    private sealed class ProgressReporter(IProgress<FolderCompareProgress>? target)
    {
        private readonly object _gate = new();
        private long _last;
        public int Completed;
        public void Report(FolderCompareProgress value, bool force = false)
        {
            if (target is null) return;
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (!force && now - _last < 100) return;
                _last = now;
                target.Report(value with { ComparedFiles = Completed });
            }
        }
        public void Complete(FolderCompareProgress value)
        {
            lock (_gate) { Completed++; Report(value); }
        }
    }

    public Task<IReadOnlyList<FolderCompareEntry>> CompareAsync(string left, string right,
        IProgress<FolderCompareProgress>? progress = null, CancellationToken cancellationToken = default,
        bool allowMissingFolders = false)
    {
        var leftRoot = Path.GetFullPath(left);
        var rightRoot = Path.GetFullPath(right);
        return Task.Run(async () =>
        {
            var reporter = new ProgressReporter(progress);
            var a = Scan(leftRoot, allowMissingFolders, cancellationToken, reporter, FolderComparePhase.ScanningLeft);
            var b = Scan(rightRoot, allowMissingFolders, cancellationToken, reporter, FolderComparePhase.ScanningRight);
            foreach (var path in a.Files.Keys.Where(b.Directories.ContainsKey).Concat(b.Files.Keys.Where(a.Directories.ContainsKey)))
                a.Failures[path] = "동일 경로에 파일과 폴더가 있습니다.";
            var paths = a.Files.Keys.Concat(b.Files.Keys).Concat(a.Directories.Keys).Concat(b.Directories.Keys).Concat(a.Failures.Keys).Concat(b.Failures.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var results = new FolderCompareEntry[paths.Length];
            reporter.Report(new("", 0, TotalEntries: paths.Length), true);
            await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length), new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
            {
                var path = paths[index];
                var leftItem = a.Files.GetValueOrDefault(path);
                var rightItem = b.Files.GetValueOrDefault(path);
                var leftPath = leftItem?.Path ?? Path.Combine(leftRoot, path);
                var rightPath = rightItem?.Path ?? Path.Combine(rightRoot, path);
                var error = FailureFor(a, path) ?? FailureFor(b, path);
                if (error is not null)
                    results[index] = new(path, leftPath, rightPath, FolderCompareStatus.Error, Error: error);
                else if (a.Directories.ContainsKey(path) || b.Directories.ContainsKey(path))
                    results[index] = new(path, leftPath, rightPath,
                        !a.Directories.ContainsKey(path) ? FolderCompareStatus.RightOnly :
                        !b.Directories.ContainsKey(path) ? FolderCompareStatus.LeftOnly : FolderCompareStatus.NotCompared,
                        IsDirectory: true, LeftWrittenUtc: a.Directories.GetValueOrDefault(path)?.Written,
                        RightWrittenUtc: b.Directories.GetValueOrDefault(path)?.Written);
                else
                    results[index] = await CompareKnownFileAsync(path, leftPath, rightPath, leftItem, rightItem, token,
                        bytes => reporter.Report(new(path, 0, TotalEntries: paths.Length, BytesRead: bytes,
                            FileBytes: leftItem?.Length ?? 0))).ConfigureAwait(false);
                reporter.Complete(new(path, 0, TotalEntries: paths.Length));
            }).ConfigureAwait(false);
            reporter.Report(new("", 0, TotalEntries: paths.Length), true);
            return (IReadOnlyList<FolderCompareEntry>)results;
        }, cancellationToken);
    }

    public async Task<FolderCompareEntry> CompareFileAsync(string relativePath, string left, string right,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await CompareKnownFileAsync(relativePath, left, right, Snapshot(left), Snapshot(right), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        { return new(relativePath, left, right, FolderCompareStatus.Error, Error: ex.Message); }
    }

    private static async Task<FolderCompareEntry> CompareKnownFileAsync(string relativePath, string left, string right,
        Item? a, Item? b, CancellationToken cancellationToken, Action<long>? readProgress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = a is null ? FolderCompareStatus.RightOnly : b is null ? FolderCompareStatus.LeftOnly
                : a.Length != b.Length ? FolderCompareStatus.Different
                : a.Length == 0 ? FolderCompareStatus.Equal
                : await EqualBytesAsync(left, right, cancellationToken, readProgress).ConfigureAwait(false)
                    ? FolderCompareStatus.Equal : FolderCompareStatus.Different;
            if (a is null && b is null) throw new IOException("양쪽 파일이 모두 없어졌습니다. 전체 새로고침을 실행하세요.");
            if (a != Snapshot(left) || b != Snapshot(right)) throw new IOException("비교 중 파일이 변경되었습니다. 다시 비교하세요.");
            return new(relativePath, left, right, status, a?.Length, b?.Length,
                LeftWrittenUtc: a?.Written, RightWrittenUtc: b?.Written);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new(relativePath, left, right, FolderCompareStatus.Error, Error: ex.Message);
        }
    }

    private static Item? Snapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var attributes = info.Attributes;
            if (attributes == (FileAttributes)(-1))
            {
                // Attributes returns -1 for missing paths; Length surfaces other access failures.
                _ = info.Length;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 파일 또는 폴더는 비교하지 않습니다.");
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("파일 경로에 폴더가 있습니다.");
            return new(path, info.Length, info.LastWriteTimeUtc);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task<bool> EqualBytesAsync(string left, string right, CancellationToken token, Action<long>? progress)
    {
        const int bufferSize = 65536;
        await using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long read = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await a.ReadAsync(leftBuffer.AsMemory(0, bufferSize), token).ConfigureAwait(false);
                if (count == 0) return await b.ReadAsync(rightBuffer.AsMemory(0, 1), token).ConfigureAwait(false) == 0;
                await b.ReadExactlyAsync(rightBuffer.AsMemory(0, count), token).ConfigureAwait(false);
                read += count;
                progress?.Invoke(read);
                if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(leftBuffer); ArrayPool<byte>.Shared.Return(rightBuffer); }
    }

    private static Inventory Scan(string root, bool allowMissing, CancellationToken token,
        ProgressReporter progress, FolderComparePhase phase)
    {
        var result = new Inventory(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        token.ThrowIfCancellationRequested();
        progress.Report(new("", 0, phase), true);
        try
        {
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.Directory) == 0) throw new IOException("폴더 경로에 파일이 있습니다.");
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("연결된 폴더는 비교하지 않습니다.");
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
                AttributesToSkip = 0, ReturnSpecialDirectories = false };
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                var path = entry.Name;
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("연결된 파일 또는 폴더는 비교하지 않습니다.");
                    if ((attributes & FileAttributes.Directory) != 0)
                        result.Directories.Add(path, new(entry.FullName, null, entry.LastWriteTimeUtc));
                    else if (!result.Files.TryAdd(path, new(entry.FullName, ((FileInfo)entry).Length, entry.LastWriteTimeUtc)))
                        result.Failures[path] = "대소문자만 다른 파일 경로가 중복됩니다.";
                }
                catch (Exception ex) when (IsFileFailure(ex)) { result.Failures[path] = ex.Message; }
                progress.Report(new(path, 0, phase, DiscoveredEntries: result.Files.Count + result.Directories.Count + result.Failures.Count));
            }
        }
        catch (DirectoryNotFoundException) when (allowMissing) { }
        catch (FileNotFoundException) when (allowMissing) { }
        catch (Exception ex) when (IsFileFailure(ex)) { result.Failures["."] = ex.Message; }
        return result;
    }

    private static string? FailureFor(Inventory inventory, string path)
    {
        if (inventory.Failures.TryGetValue(".", out var rootError)) return rootError;
        var candidate = path;
        while (!string.IsNullOrEmpty(candidate))
        {
            if (inventory.Failures.TryGetValue(candidate, out var error)) return error;
            candidate = Path.GetDirectoryName(candidate);
        }
        return null;
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

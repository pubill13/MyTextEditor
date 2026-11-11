namespace MyTextEditor.Core;

public enum FolderCompareStatus { Equal, Different, LeftOnly, RightOnly, Error }

public sealed record FolderCompareEntry(string RelativePath, string LeftPath, string RightPath,
    FolderCompareStatus Status, long? LeftLength = null, long? RightLength = null, string? Error = null);

public sealed record FolderCompareProgress(string RelativePath, int ComparedFiles);

public sealed class FolderCompareService
{
    private sealed record Inventory(Dictionary<string, string> Files, HashSet<string> Directories,
        Dictionary<string, string> Failures);

    public Task<IReadOnlyList<FolderCompareEntry>> CompareAsync(string left, string right,
        IProgress<FolderCompareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var leftRoot = Path.GetFullPath(left);
        var rightRoot = Path.GetFullPath(right);
        return Task.Run(async () =>
        {
            var a = Scan(leftRoot, cancellationToken);
            var b = Scan(rightRoot, cancellationToken);
            foreach (var path in a.Files.Keys.Where(b.Directories.Contains).Concat(b.Files.Keys.Where(a.Directories.Contains)))
                a.Failures[path] = "동일 경로에 파일과 폴더가 있습니다.";
            var paths = a.Files.Keys.Concat(b.Files.Keys).Concat(a.Failures.Keys).Concat(b.Failures.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            var results = new List<FolderCompareEntry>();
            var lastProgress = Environment.TickCount64;
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftPath = a.Files.GetValueOrDefault(path) ?? Path.Combine(leftRoot, path);
                var rightPath = b.Files.GetValueOrDefault(path) ?? Path.Combine(rightRoot, path);
                var error = FailureFor(a, path) ?? FailureFor(b, path);
                if (error is null && (a.Directories.Contains(path) || b.Directories.Contains(path)))
                    error = "동일 경로에 파일과 폴더가 있습니다.";
                results.Add(error is not null
                    ? new(path, leftPath, rightPath, FolderCompareStatus.Error, Error: error)
                    : await CompareFileAsync(path, leftPath, rightPath, cancellationToken).ConfigureAwait(false));
                if (Environment.TickCount64 - lastProgress >= 100)
                {
                    progress?.Report(new(path, results.Count));
                    lastProgress = Environment.TickCount64;
                }
            }
            return (IReadOnlyList<FolderCompareEntry>)results;
        }, cancellationToken);
    }

    public async Task<FolderCompareEntry> CompareFileAsync(string relativePath, string left, string right,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = Snapshot(left);
            var b = Snapshot(right);
            var status = a is null ? FolderCompareStatus.RightOnly : b is null ? FolderCompareStatus.LeftOnly
                : a.Value.Length != b.Value.Length ? FolderCompareStatus.Different
                : await EqualBytesAsync(left, right, cancellationToken).ConfigureAwait(false)
                    ? FolderCompareStatus.Equal : FolderCompareStatus.Different;
            if (a is null && b is null) throw new IOException("양쪽 파일이 모두 없어졌습니다. 전체 새로고침을 실행하세요.");
            if (a != Snapshot(left) || b != Snapshot(right)) throw new IOException("비교 중 파일이 변경되었습니다. 다시 비교하세요.");
            return new(relativePath, left, right, status, a?.Length, b?.Length);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new(relativePath, left, right, FolderCompareStatus.Error, Error: ex.Message);
        }
    }

    private static (long Length, DateTime Written)? Snapshot(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("연결된 파일 또는 폴더는 비교하지 않습니다.");
            if ((attributes & FileAttributes.Directory) != 0) throw new IOException("파일 경로에 폴더가 있습니다.");
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task<bool> EqualBytesAsync(string left, string right, CancellationToken token)
    {
        await using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftBuffer = new byte[65536];
        var rightBuffer = new byte[65536];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await a.ReadAsync(leftBuffer, token).ConfigureAwait(false);
            if (count == 0) return await b.ReadAsync(rightBuffer.AsMemory(0, 1), token).ConfigureAwait(false) == 0;
            await b.ReadExactlyAsync(rightBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
        }
    }

    private static Inventory Scan(string root, CancellationToken token)
    {
        var result = new Inventory(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, directory);
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결된 폴더는 비교하지 않습니다.");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.GetRelativePath(root, entry);
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("연결된 파일 또는 폴더는 비교하지 않습니다.");
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            result.Directories.Add(path);
                            pending.Push(entry);
                        }
                        else if (!result.Files.TryAdd(path, entry))
                            result.Failures[path] = "대소문자만 다른 파일 경로가 중복됩니다.";
                    }
                    catch (Exception ex) when (IsFileFailure(ex)) { result.Failures[path] = ex.Message; }
                }
            }
            catch (Exception ex) when (IsFileFailure(ex)) { result.Failures[relative] = ex.Message; }
        }
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

using System.IO.Enumeration;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed record FolderSearchOptions(bool IncludeSubdirectories = true,
    string FilePatterns = "*.txt;*.log;*.csv;*.md;*.json;*.xml;*.yaml;*.yml;*.ini;*.cfg;*.cs;*.py;*.js;*.ts;*.sql;*.out");
public sealed record FolderSearchFileResult(string FilePath, string NewLine, long Length,
    DateTime LastWriteUtc, IReadOnlyList<SearchResult> Matches);
public sealed record FolderSearchFailure(string FilePath, string Message);
public sealed record FolderSearchProgress(string FilePath, int FilesScanned, int MatchingLines);
public sealed record FolderSearchResult(IReadOnlyList<FolderSearchFileResult> Files,
    IReadOnlyList<FolderSearchFailure> Failures, int FilesScanned);

public sealed class FolderTextSearchService
{
    public Task<FolderSearchResult> SearchAsync(string folder, ConditionNode condition,
        SearchOptions? searchOptions = null, FolderSearchOptions? folderOptions = null,
        IProgress<FolderSearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(condition);
        folderOptions ??= new FolderSearchOptions();
        var patterns = folderOptions.FilePatterns.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (patterns.Length == 0 || patterns.Any(p => p.IndexOfAny(['/', '\\', ':']) >= 0))
            throw new ArgumentException("파일 패턴은 *.txt;*.log처럼 파일 이름 패턴으로 입력하세요.", nameof(folderOptions));
        var root = Path.GetFullPath(folder);
        return Task.Run(async () =>
        {
            var files = new List<FolderSearchFileResult>();
            var failures = new List<FolderSearchFailure>();
            var pending = new Stack<string>();
            pending.Push(root);
            var scanned = 0;
            var matchCount = 0;
            var loader = new DocumentFileService();
            var engine = new TextSearchEngine();
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Do not follow links, including an explicitly selected junction root.
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        failures.Add(new(directory, "연결된 폴더는 검색하지 않습니다."));
                        continue;
                    }
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (folderOptions.IncludeSubdirectories && (attributes & FileAttributes.ReparsePoint) == 0)
                                    pending.Push(entry);
                                continue;
                            }
                            if (!patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(entry), ignoreCase: true))) continue;
                            progress?.Report(new(entry, scanned, matchCount));
                            var before = new FileInfo(entry);
                            var length = before.Length;
                            var written = before.LastWriteTimeUtc;
                            var document = await loader.LoadAsync(entry, cancellationToken).ConfigureAwait(false);
                            var matches = engine.Search(document.Text, condition, searchOptions, cancellationToken);
                            var after = new FileInfo(entry);
                            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
                                failures.Add(new(entry, "검색 중 파일이 변경되어 결과에서 제외했습니다."));
                            else if (matches.Count > 0)
                            {
                                files.Add(new(entry, document.NewLine, length, written, matches));
                                matchCount += matches.Count;
                            }
                            scanned++;
                            progress?.Report(new(entry, scanned, matchCount));
                        }
                        catch (Exception ex) when (IsFileFailure(ex)) { failures.Add(new(entry, ex.Message)); }
                    }
                }
                catch (Exception ex) when (IsFileFailure(ex)) { failures.Add(new(directory, ex.Message)); }
            }
            return new FolderSearchResult(files, failures, scanned);
        }, cancellationToken);
    }

    private static bool IsFileFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.Text.DecoderFallbackException;
}

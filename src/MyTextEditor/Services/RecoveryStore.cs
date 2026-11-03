using System.IO;
using System.Text;
using System.Text.Json;
using MyTextEditor.Core;

namespace MyTextEditor.Services;

public sealed record RecoverySnapshot(Guid DocumentId, string? FilePath, DocumentEncodingKind Encoding,
    string NewLine, int SelectionStart, int SelectionLength, string Text, DateTimeOffset CapturedAt);

public sealed record RecoveryDocumentInfo(Guid DocumentId, string? FilePath, DocumentEncodingKind Encoding,
    string NewLine, int SelectionStart, int SelectionLength, DateTimeOffset CapturedAt, long Utf8ByteLength);

/// <summary>Each process owns a session lock; only abandoned sessions can be offered for recovery.</summary>
public sealed class RecoveryStore : IDisposable, IAsyncDisposable
{
    private readonly string _root;
    private readonly RecoverySessionFiles _session;

    public RecoveryStore(string? root = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor", "Recovery"));
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid().ToString("N");
        // Acquire the lock before making the directory discoverable by another process.
        var lease = new FileStream(Path.Combine(_root, id + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var directory = Path.Combine(_root, id);
            Directory.CreateDirectory(directory);
            _session = new RecoverySessionFiles(directory, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public Task WriteAsync(RecoverySnapshot snapshot, CancellationToken token = default) => _session.WriteAsync(snapshot, token);
    public Task DiscardAsync(Guid documentId, CancellationToken token = default) => _session.DiscardAsync(documentId, token);

    public Task<IReadOnlyList<RecoveryClaim>> ClaimOrphanSessionsAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<RecoveryClaim>>(() =>
    {
        var claims = new List<RecoveryClaim>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name, "N", out _)) continue;
                FileStream lease;
                try { lease = new FileStream(Path.Combine(_root, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33) { continue; }
                var files = new RecoverySessionFiles(directory, lease);
                try
                {
                    var claim = new RecoveryClaim(files);
                    if (claim.Documents.Count > 0 || claim.Issues.Count > 0) claims.Add(claim);
                    else claim.Dispose();
                }
                catch { files.Dispose(); throw; }
            }
            return claims;
        }
        catch
        {
            foreach (var claim in claims) claim.Dispose();
            throw;
        }
    }, token);

    // Closing the process must leave unsaved documents available to the next launch.
    public void Dispose() => _session.Dispose();
    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

public sealed class RecoveryClaim : IDisposable, IAsyncDisposable
{
    private readonly RecoverySessionFiles _files;
    public IReadOnlyList<RecoveryDocumentInfo> Documents { get; }
    public IReadOnlyList<string> Issues { get; }

    internal RecoveryClaim(RecoverySessionFiles files)
    {
        _files = files;
        (Documents, Issues) = files.ReadCatalog();
    }

    public Task<RecoverySnapshot> ReadAsync(Guid documentId, CancellationToken token = default) => _files.ReadAsync(documentId, token);
    public Task DiscardAsync(Guid documentId, CancellationToken token = default) => _files.DiscardAsync(documentId, token);
    public void Dispose() => _files.Dispose();
    public ValueTask DisposeAsync() => _files.DisposeAsync();
}

internal sealed class RecoverySessionFiles(string directory, FileStream lease) : IDisposable, IAsyncDisposable
{
    private const int FormatVersion = 1;
    private const int MaximumHeaderBytes = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private sealed record Header(int Version, RecoveryDocumentInfo Document);

    private string DocumentPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".recovery");

    public Task WriteAsync(RecoverySnapshot snapshot, CancellationToken token) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.DocumentId == Guid.Empty || !Enum.IsDefined(snapshot.Encoding) ||
            snapshot.NewLine is not ("\r\n" or "\n" or "\r") || snapshot.SelectionStart < 0 || snapshot.SelectionLength < 0)
            throw new ArgumentException("복구 문서 정보가 올바르지 않습니다.", nameof(snapshot));
        var info = new RecoveryDocumentInfo(snapshot.DocumentId, snapshot.FilePath, snapshot.Encoding, snapshot.NewLine,
            snapshot.SelectionStart, snapshot.SelectionLength, snapshot.CapturedAt, Utf8.GetByteCount(snapshot.Text));
        var header = JsonSerializer.SerializeToUtf8Bytes(new Header(FormatVersion, info));
        if (header.Length >= MaximumHeaderBytes) throw new IOException("복구 문서 정보가 너무 큽니다.");
        var path = DocumentPath(snapshot.DocumentId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(header, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
                await using (var writer = new StreamWriter(stream, Utf8, 65536, leaveOpen: true))
                {
                    await writer.WriteAsync(snapshot.Text.AsMemory(), token).ConfigureAwait(false);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, token);

    public Task DiscardAsync(Guid id, CancellationToken token) => RunAsync(() =>
    {
        File.Delete(DocumentPath(id));
        return Task.CompletedTask;
    }, token);

    public Task<RecoverySnapshot> ReadAsync(Guid id, CancellationToken token) => RunAsync(async () =>
    {
        await using var stream = new FileStream(DocumentPath(id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var info = ReadHeader(stream, id);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        string text;
        try { text = await reader.ReadToEndAsync(token).ConfigureAwait(false); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("복구 파일의 본문 인코딩이 올바르지 않습니다.", exception); }
        return new RecoverySnapshot(info.DocumentId, info.FilePath, info.Encoding, info.NewLine,
            Math.Min(info.SelectionStart, text.Length), Math.Min(info.SelectionLength, text.Length - Math.Min(info.SelectionStart, text.Length)), text, info.CapturedAt);
    }, token);

    public (IReadOnlyList<RecoveryDocumentInfo>, IReadOnlyList<string>) ReadCatalog()
    {
        var documents = new List<RecoveryDocumentInfo>();
        var issues = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.recovery"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                documents.Add(ReadHeader(stream, id));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Never echo an invalid JSON header or document body into diagnostics.
                issues.Add($"복구 파일을 읽지 못했습니다 ({id:N}, {exception.GetType().Name}). 원본 복구 파일은 유지합니다.");
            }
        }
        return (documents.OrderByDescending(document => document.CapturedAt).ToArray(), issues.ToArray());
    }

    private static RecoveryDocumentInfo ReadHeader(FileStream stream, Guid expectedId)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0 || buffer.Length >= MaximumHeaderBytes) throw new InvalidDataException("복구 파일 헤더가 잘못되었습니다.");
            if (value == '\n') break;
            buffer.WriteByte((byte)value);
        }
        var header = JsonSerializer.Deserialize<Header>(buffer.ToArray());
        if (header?.Version != FormatVersion || header.Document is not { } info || info.DocumentId != expectedId ||
            !Enum.IsDefined(info.Encoding) || info.NewLine is not ("\r\n" or "\n" or "\r") ||
            info.SelectionStart < 0 || info.SelectionLength < 0 || info.Utf8ByteLength < 0 ||
            stream.Length - stream.Position != info.Utf8ByteLength)
            throw new InvalidDataException("복구 파일 형식 또는 길이가 올바르지 않습니다.");
        return info;
    }

    private Task RunAsync(Func<Task> action, CancellationToken token) => RunAsync(async () => { await action().ConfigureAwait(false); return true; }, token);

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        // Queue at call time, not on a worker: a later discard must not run ahead of a write.
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            return await Task.Run(action, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        // Do not release the process lease while a queued write can still publish a file.
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; lease.Dispose(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await lease.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

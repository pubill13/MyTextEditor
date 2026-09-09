using System.Text;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Diff;

public enum DiffEndpointKind
{
    OpenDocument,
    File,
    Clipboard,
    Selection
}

public sealed class DiffEndpoint
{
    public required DiffEndpointKind Kind { get; init; }
    public required string DisplayName { get; set; }
    public required string Text { get; set; }
    public string? FilePath { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool HasByteOrderMark { get; set; }
    public string NewLine { get; set; } = "\r\n";
    public bool IsReadOnly { get; set; }
    public Guid? SourceDocumentId { get; init; }
    public long SourceRevision { get; set; }
    public long? SourceFileLength { get; set; }
    public DateTime? SourceFileLastWriteUtc { get; set; }
}

public sealed record DiffWindowOptions(
    bool IgnoreWhitespace = false,
    bool IgnoreCase = false,
    bool IgnoreEmptyLines = false,
    bool ScrollSync = true,
    double Width = 1320,
    double Height = 820,
    double? Left = null,
    double? Top = null);

public sealed record DiffAppearance(string FontFamily, float FontSize, bool DarkTheme, float DpiScale = 1f);

public sealed record DiffSourceSnapshot(string DisplayName, string Text, string? FilePath,
    Encoding Encoding, bool HasByteOrderMark, string NewLine, long Revision);

public sealed class DiffWindowCallbacks
{
    public required Func<Guid, long> GetSourceRevision { get; init; }
    public required Func<Guid, DiffSourceSnapshot?> GetSourceSnapshot { get; init; }
    public required Func<Guid, string, long, bool, Task<bool>> ApplyToSourceAsync { get; init; }
    public required Func<Guid, Task<bool>> SaveSourceAsync { get; init; }
    public required Func<string, string, Task> CreateDocumentAsync { get; init; }
    public required Action<DiffWindowOptions> SettingsChanged { get; init; }
    public Action<DiffEndpoint, DiffEndpoint>? OpenChildWindow { get; init; }
}

public sealed record DiffSourceSelection(DiffEndpointKind Kind, Guid? DocumentId = null, string? FilePath = null);

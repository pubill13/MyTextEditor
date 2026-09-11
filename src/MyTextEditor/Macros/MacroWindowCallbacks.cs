namespace MyTextEditor.Macros;

public sealed record MacroDocumentSnapshot(Guid DocumentId, long Revision, string Text, string NewLine, string DisplayName);

public sealed class MacroWindowCallbacks
{
    public required Func<MacroDocumentSnapshot?> GetCurrentDocument { get; init; }
    public required Func<Guid, long, string, bool> ApplyResult { get; init; }
    public Action<string>? ReportStatus { get; init; }
    public Action? ShowHelp { get; init; }
}

namespace MyTextEditor.Macros;

public sealed record MacroDocumentSnapshot(Guid DocumentId, long Revision, string Text, string NewLine, string DisplayName);
public sealed record MacroDocumentUndoState(Guid DocumentId, string DisplayName, bool CanUndo);

public sealed class MacroWindowCallbacks
{
    public required Func<MacroDocumentSnapshot?> GetCurrentDocument { get; init; }
    public required Func<Guid, long, string, bool> ApplyResult { get; init; }
    public Func<MacroDocumentUndoState?>? GetCurrentUndoState { get; init; }
    public Action? UndoCurrentDocument { get; init; }
    public Action<string>? ReportStatus { get; init; }
    public Action? ShowHelp { get; init; }
}

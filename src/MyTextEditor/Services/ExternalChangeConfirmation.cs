using System.Windows;

namespace MyTextEditor.Services;

// Shared by editor and comparison windows, which can poll on the same dispatcher.
internal static class ExternalChangeConfirmation
{
    private static readonly Dictionary<Guid, long> ApprovedDocumentReloads = [];
    private static bool _promptOpen;
    internal static Func<Window, string, bool, bool>? ConfirmOverride { get; set; }

    public static bool? TryConfirm(Window owner, string name, bool hasUnsavedChanges)
    {
        if (_promptOpen) return null;
        _promptOpen = true;
        try
        {
            if (ConfirmOverride is { } confirm) return confirm(owner, name, hasUnsavedChanges);
            var warning = hasUnsavedChanges
                ? "\n\n저장하지 않은 현재 수정 내용은 버려집니다."
                : string.Empty;
            return System.Windows.MessageBox.Show(owner,
                $"{name}의 원본이 변경되었습니다. 최신 내용을 불러올까요?{warning}\n\n예: 최신 내용 불러오기\n아니요: 현재 내용 유지",
                "외부 변경 감지", MessageBoxButton.YesNo,
                hasUnsavedChanges ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        finally { _promptOpen = false; }
    }

    public static void RememberDocumentReload(Guid documentId, long revision) => ApprovedDocumentReloads[documentId] = revision;
    public static bool WasDocumentReloadApproved(Guid documentId, long revision) =>
        ApprovedDocumentReloads.TryGetValue(documentId, out var approved) && approved == revision;
    public static void ForgetDocument(Guid documentId) => ApprovedDocumentReloads.Remove(documentId);
}

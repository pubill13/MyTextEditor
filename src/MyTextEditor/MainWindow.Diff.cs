using System.Text;
using System.Windows;
using System.Windows.Media;
using MyTextEditor.Diff;
using MyTextEditor.Models;

namespace MyTextEditor;

public partial class MainWindow
{
    private DiffWorkspaceWindow? _diffWorkspaceWindow;

    private void OpenDiff_Click(object sender, RoutedEventArgs e)
    {
        var workspace = EnsureDiffWorkspace();
        workspace.OpenEmptyTab(reuseExisting: true);
        ActivateDiffWorkspace(workspace);
    }

    private void CompareClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null)
        {
            StatusMessage.Text = "클립보드와 비교할 문서를 먼저 여세요.";
            return;
        }
        if (!System.Windows.Clipboard.ContainsText())
        {
            StatusMessage.Text = "클립보드에 비교할 텍스트가 없습니다.";
            return;
        }

        OpenDiffComparison(CreateDocumentEndpoint(CurrentDocument), new DiffEndpoint
        {
            Kind = DiffEndpointKind.Clipboard,
            DisplayName = "클립보드",
            Text = System.Windows.Clipboard.GetText(),
            IsReadOnly = true,
            NewLine = CurrentDocument.NewLine
        });
    }

    private void CompareSelectionClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentDocument is null || CurrentEditor?.HasSelection != true)
        {
            StatusMessage.Text = "현재 문서에서 비교할 영역을 먼저 선택하세요.";
            return;
        }
        if (!System.Windows.Clipboard.ContainsText())
        {
            StatusMessage.Text = "클립보드에 비교할 텍스트가 없습니다.";
            return;
        }

        OpenDiffComparison(new DiffEndpoint
        {
            Kind = DiffEndpointKind.Selection,
            DisplayName = $"{CurrentDocument.DisplayName} 선택 영역",
            Text = CurrentEditor.SelectedText,
            IsReadOnly = true,
            NewLine = CurrentDocument.NewLine
        }, new DiffEndpoint
        {
            Kind = DiffEndpointKind.Clipboard,
            DisplayName = "클립보드",
            Text = System.Windows.Clipboard.GetText(),
            IsReadOnly = true,
            NewLine = CurrentDocument.NewLine
        });
    }

    private static DiffEndpoint CreateDocumentEndpoint(DocumentViewModel document) => new()
    {
        Kind = DiffEndpointKind.OpenDocument,
        DisplayName = document.DisplayName,
        Text = document.Text,
        FilePath = document.FilePath,
        Encoding = document.Encoding,
        HasByteOrderMark = document.HasByteOrderMark,
        NewLine = document.NewLine,
        SourceDocumentId = document.Id,
        SourceRevision = document.ContentRevision
    };

    private DiffWorkspaceWindow EnsureDiffWorkspace()
    {
        if (_diffWorkspaceWindow is { IsLoaded: true } existing)
            return existing;

        var workspace = new DiffWorkspaceWindow(GetDiffOptions(), CreateDiffCallbacks(), GetDiffAppearance()) { Owner = this };
        workspace.ResourcesReleased += DiffWorkspace_ResourcesReleased;
        workspace.Closed += DiffWorkspace_Closed;
        _diffWorkspaceWindow = workspace;
        workspace.Show();
        return workspace;
    }

    private static void ActivateDiffWorkspace(DiffWorkspaceWindow workspace)
    {
        if (workspace.WindowState == WindowState.Minimized)
            workspace.WindowState = WindowState.Normal;
        workspace.Activate();
    }

    private void OpenDiffComparison(DiffEndpoint left, DiffEndpoint right)
    {
        var workspace = EnsureDiffWorkspace();
        workspace.OpenComparison(left, right);
        ActivateDiffWorkspace(workspace);
    }

    private void DiffWorkspace_ResourcesReleased(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _diffWorkspaceWindow))
            _diffWorkspaceWindow = null;
    }

    private void DiffWorkspace_Closed(object? sender, EventArgs e)
    {
        if (sender is DiffWorkspaceWindow workspace)
        {
            workspace.ResourcesReleased -= DiffWorkspace_ResourcesReleased;
            workspace.Closed -= DiffWorkspace_Closed;
        }
        if (ReferenceEquals(sender, _diffWorkspaceWindow))
            _diffWorkspaceWindow = null;
    }

    private DiffWindowOptions GetDiffOptions() => new(
        _settings.Diff.IgnoreWhitespace,
        _settings.Diff.IgnoreCase,
        _settings.Diff.IgnoreEmptyLines,
        _settings.Diff.ScrollSync,
        _settings.Diff.WindowWidth,
        _settings.Diff.WindowHeight,
        _settings.Diff.WindowLeft,
        _settings.Diff.WindowTop);

    private DiffAppearance GetDiffAppearance() => new(
        _settings.EditorFontFamily,
        (float)_settings.EditorFontSize,
        _settings.Theme == "Dark",
        IsLoaded ? (float)VisualTreeHelper.GetDpi(this).DpiScaleX : 1f);

    private DiffWindowCallbacks CreateDiffCallbacks() => new()
    {
        GetSourceRevision = id => FindDocument(id)?.ContentRevision ?? -1,
        GetSourceSnapshot = GetDiffSourceSnapshot,
        GetOpenDocuments = () => Documents.Select(CreateDiffSourceSnapshot).ToArray(),
        ApplyToSourceAsync = ApplyDiffToSourceAsync,
        SaveSourceAsync = SaveDiffSourceAsync,
        CreateDocumentAsync = CreateDiffDocumentAsync,
        SettingsChanged = SaveDiffOptions,
        ShowHelp = ShowHelpWindow
    };

    private DocumentViewModel? FindDocument(Guid id) => Documents.FirstOrDefault(document => document.Id == id);

    private static DiffSourceSnapshot CreateDiffSourceSnapshot(DocumentViewModel document) => new(
        document.DisplayName,
        document.Text,
        document.FilePath,
        document.Encoding,
        document.HasByteOrderMark,
        document.NewLine,
        document.ContentRevision,
        document.Id);

    private DiffSourceSnapshot? GetDiffSourceSnapshot(Guid id)
    {
        var document = FindDocument(id);
        return document is null ? null : CreateDiffSourceSnapshot(document);
    }

    private Task<bool> ApplyDiffToSourceAsync(Guid id, string text, long expectedRevision, bool force)
    {
        var document = FindDocument(id);
        if (document is null || !force && document.ContentRevision != expectedRevision)
            return Task.FromResult(false);

        document.Editor.ReplaceAll(text);
        DocumentTabs.SelectedItem = document;
        return Task.FromResult(true);
    }

    private async Task<bool> SaveDiffSourceAsync(Guid id)
    {
        var document = FindDocument(id);
        return document is not null && await SaveDocumentAsync(document, false);
    }

    private Task CreateDiffDocumentAsync(string title, string text)
    {
        NewDocument(text);
        StatusMessage.Text = $"Diff 내용으로 새 문서를 만들었습니다: {title}";
        return Task.CompletedTask;
    }

    private void SaveDiffOptions(DiffWindowOptions options)
    {
        _settings.Diff.IgnoreWhitespace = options.IgnoreWhitespace;
        _settings.Diff.IgnoreCase = options.IgnoreCase;
        _settings.Diff.IgnoreEmptyLines = options.IgnoreEmptyLines;
        _settings.Diff.ScrollSync = options.ScrollSync;
        _settings.Diff.WindowWidth = options.Width;
        _settings.Diff.WindowHeight = options.Height;
        _settings.Diff.WindowLeft = options.Left;
        _settings.Diff.WindowTop = options.Top;
        MarkSettingsDirty();
    }

    private void ApplyDiffAppearanceToWindows() => _diffWorkspaceWindow?.ApplyAppearance(GetDiffAppearance());

    private void RefreshDiffSourceStates() => _diffWorkspaceWindow?.RefreshSourceStates();

    private async Task<bool> ConfirmCloseDiffWindowsAsync() =>
        _diffWorkspaceWindow is null || await _diffWorkspaceWindow.RequestCloseAsync();

    private void ResetDiffCloseApprovals() => _diffWorkspaceWindow?.CancelPreparedClose();
}

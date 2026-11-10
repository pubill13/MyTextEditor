using System.Windows;
using MyTextEditor.Help;
using MyTextEditor.Models;
using MyTextEditor.Services;

namespace MyTextEditor.Diff;

public partial class DiffWorkspaceWindow
{
    private HelpWindow? _standaloneHelpWindow;

    public void DetachFromMainWindow(Func<Guid, bool> sourceHasUnsavedChanges, bool persistSettings = true)
    {
        Owner = null;
        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1,
            GetSourceSnapshot = _ => null,
            GetOpenDocuments = () => [],
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false),
            CreateDocumentAsync = (title, text) =>
            {
                var window = new MainWindow(createDocument: false);
                window.Show();
                window.Activate();
                return window.CreateDiffDocumentAsync(title, text);
            },
            SettingsChanged = options => SaveStandaloneSettings(settings =>
            {
                settings.Diff.IgnoreWhitespace = options.IgnoreWhitespace;
                settings.Diff.IgnoreCase = options.IgnoreCase;
                settings.Diff.IgnoreEmptyLines = options.IgnoreEmptyLines;
                settings.Diff.ScrollSync = options.ScrollSync;
                settings.Diff.WindowWidth = options.Width;
                settings.Diff.WindowHeight = options.Height;
                settings.Diff.WindowLeft = options.Left;
                settings.Diff.WindowTop = options.Top;
            }, persistSettings),
            FontSizeChanged = size => SaveStandaloneSettings(settings => settings.Diff.FontSize = size, persistSettings),
            HighlightColor = _highlightColor,
            HighlightColorChanged = color =>
            {
                SetHighlightColor(color);
                SaveStandaloneSettings(settings => settings.HighlightColor = color, persistSettings);
            },
            ShowHelp = ShowStandaloneHelp
        };
        foreach (var view in Views) view.DetachSourceDocuments(callbacks, sourceHasUnsavedChanges);
        _callbacks = callbacks;
        // A workspace hidden by the main window's toggle must remain reachable after it closes.
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    }

    private void SaveStandaloneSettings(Action<UserSettings> update, bool enabled)
    {
        if (!enabled) return;
        var loaded = SettingsService.LoadWithResult();
        if (loaded.Error is not null)
        {
            StatusText.Text = $"설정을 읽지 못해 변경을 저장하지 않았습니다: {loaded.Error.Message}";
            return;
        }
        update(loaded.Settings);
        var result = SettingsService.TrySave(loaded.Settings);
        if (!result.Succeeded) StatusText.Text = $"설정을 저장하지 못했습니다: {result.Error?.Message}";
    }

    private void ShowStandaloneHelp()
    {
        if (_standaloneHelpWindow is null)
        {
            var window = new HelpWindow { Owner = this };
            window.Resources.MergedDictionaries.Add(Resources);
            window.Closed += (_, _) => _standaloneHelpWindow = null;
            _standaloneHelpWindow = window;
            window.Show();
        }
        _standaloneHelpWindow.NavigateToTopic("diff");
    }
}

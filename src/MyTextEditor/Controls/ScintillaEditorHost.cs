using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Windows.Forms.Integration;
using ScintillaNET;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;

namespace MyTextEditor.Controls;

public enum EditorShortcut
{
    NewDocument,
    OpenDocument,
    SaveDocument,
    SaveDocumentAs,
    CloseDocument,
    CloseAllDocuments,
    Find,
    Replace,
    NextDocument,
    PreviousDocument,
    FindNext,
    FindPrevious
}

public sealed class EditorShortcutEventArgs(EditorShortcut shortcut) : EventArgs
{
    public EditorShortcut Shortcut { get; } = shortcut;
}

public sealed class ScintillaEditorHost : WindowsFormsHost
{
    private const int SciAddText = 2001;
    private const int SciClearAll = 2004;
    private const int SciSetUndoCollection = 2012;
    private const int SciSetCodePage = 2037;
    private const int SciAllocate = 2446;
    private const int Utf8CodePage = 65001;

    private readonly ShortcutScintilla _editor;
    private bool _loading;
    private bool _resourcesReleased;

    public ScintillaEditorHost()
    {
        EnsureNativeLibraries();
        _editor = new ShortcutScintilla
        {
            Dock = Forms.DockStyle.Fill,
            BorderStyle = ScintillaNET.BorderStyle.None,
            WrapMode = WrapMode.None,
            AllowDrop = true
        };
        _editor.SetShortcutProcessor(ProcessShortcut);
        _editor.Margins[0].Type = MarginType.Number;
        _editor.Margins[0].Width = 44;
        _editor.TextChanged += Editor_TextChanged;
        _editor.UpdateUI += Editor_UpdateUI;
        _editor.SavePointLeft += Editor_SavePointChanged;
        _editor.SavePointReached += Editor_SavePointChanged;
        _editor.DragEnter += Editor_DragEnter;
        _editor.DragDrop += Editor_DragDrop;
        Child = _editor;
    }

    private static void EnsureNativeLibraries()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor", "native", "7.0.0", "win-x64");
        Directory.CreateDirectory(directory);
        ExtractResource("MyTextEditor.Native.win-x64.Scintilla.dll", Path.Combine(directory, "Scintilla.dll"));
        ExtractResource("MyTextEditor.Native.win-x64.Lexilla.dll", Path.Combine(directory, "Lexilla.dll"));
        ScintillaNativeLibrary.SatelliteDirectory = directory;
    }

    private static void ExtractResource(string resourceName, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"내장 네이티브 리소스를 찾을 수 없습니다: {resourceName}");
        if (File.Exists(destination) && new FileInfo(destination).Length == resource.Length) return;
        var temporary = destination + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public long ContentRevision { get; private set; }
    public bool IsModified => _editor.Modified;
    public bool CanUndo => _editor.CanUndo;
    public bool CanRedo => _editor.CanRedo;
    public int LineCount => _editor.Lines.Count;
    public int CurrentLine => _editor.LineFromPosition(_editor.CurrentPosition);
    public int CurrentColumn => _editor.GetColumn(_editor.CurrentPosition);

    public event EventHandler? CaretChanged;
    public event EventHandler? RevisionChanged;
    public event EventHandler? DirtyChanged;
    public event EventHandler<EditorShortcutEventArgs>? ShortcutRequested;
    public event Action<IReadOnlyList<string>>? FilesDropped;

    public void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        _editor.SetShortcutProcessor(null);
        _editor.TextChanged -= Editor_TextChanged;
        _editor.UpdateUI -= Editor_UpdateUI;
        _editor.SavePointLeft -= Editor_SavePointChanged;
        _editor.SavePointReached -= Editor_SavePointChanged;
        _editor.DragEnter -= Editor_DragEnter;
        _editor.DragDrop -= Editor_DragDrop;
        Child = null;
        _editor.Dispose();
        CaretChanged = null;
        RevisionChanged = null;
        DirtyChanged = null;
        ShortcutRequested = null;
        FilesDropped = null;
    }

    public unsafe void LoadUtf8(ReadOnlyMemory<byte> utf8)
    {
        _editor.CreateControl();
        _loading = true;
        try
        {
            _editor.DirectMessage(SciSetCodePage, (IntPtr)Utf8CodePage);
            _editor.DirectMessage(SciSetUndoCollection, IntPtr.Zero);
            _editor.DirectMessage(SciClearAll);
            _editor.DirectMessage(SciAllocate, (IntPtr)utf8.Length);
            if (!utf8.IsEmpty)
            {
                fixed (byte* pointer = utf8.Span)
                    _editor.DirectMessage(SciAddText, (IntPtr)utf8.Length, (IntPtr)pointer);
            }
            _editor.DirectMessage(SciSetUndoCollection, (IntPtr)1);
            _editor.EmptyUndoBuffer();
            _editor.SetSavePoint();
            ContentRevision = 0;
        }
        finally
        {
            _loading = false;
        }
        CaretChanged?.Invoke(this, EventArgs.Empty);
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetText() => _editor.Text;

    public void ReplaceAll(string text)
    {
        var revision = ContentRevision;
        _editor.BeginUndoAction();
        try
        {
            _editor.SelectAll();
            _editor.ReplaceSelection(text);
        }
        finally
        {
            _editor.EndUndoAction();
        }
        if (ContentRevision == revision)
        {
            ContentRevision++;
            RevisionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void GoToLine(int oneBasedLineNumber)
    {
        if (_editor.Lines.Count == 0) return;
        var index = Math.Clamp(oneBasedLineNumber - 1, 0, _editor.Lines.Count - 1);
        var line = _editor.Lines[index];
        _editor.SetSelection(line.EndPosition, line.Position);
        _editor.ScrollCaret();
        _editor.Focus();
    }

    public void Undo() => _editor.Undo();
    public void Redo() => _editor.Redo();
    public void SelectAll() => _editor.SelectAll();
    public void FocusEditor() => _editor.Focus();

    public void MarkSaved()
    {
        _editor.SetSavePoint();
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetNewLine(string newLine) => _editor.EolMode = newLine switch
    {
        "\n" => Eol.Lf,
        "\r" => Eol.Cr,
        _ => Eol.CrLf
    };

    public void ApplyAppearance(string fontFamily, float fontSize, bool darkTheme, float dpiScale = 1f)
    {
        var editorBack = darkTheme ? DrawingColor.FromArgb(25, 31, 41) : DrawingColor.White;
        var editorFore = darkTheme ? DrawingColor.FromArgb(236, 240, 246) : DrawingColor.FromArgb(32, 38, 49);
        var marginBack = darkTheme ? DrawingColor.FromArgb(17, 21, 28) : DrawingColor.FromArgb(247, 249, 252);
        var marginFore = darkTheme ? DrawingColor.FromArgb(163, 174, 194) : DrawingColor.FromArgb(102, 112, 133);
        var selectionBack = darkTheme ? DrawingColor.FromArgb(41, 70, 119) : DrawingColor.FromArgb(220, 232, 255);
        var inactiveSelectionBack = darkTheme ? DrawingColor.FromArgb(48, 58, 74) : DrawingColor.FromArgb(226, 230, 237);
        var style = _editor.Styles[ScintillaNET.Style.Default];
        style.Font = fontFamily;
        style.SizeF = Math.Max(7f, fontSize);
        style.ForeColor = editorFore;
        style.BackColor = editorBack;
        _editor.StyleClearAll();
        _editor.BackColor = editorBack;
        _editor.ForeColor = editorFore;
        _editor.CaretForeColor = darkTheme ? DrawingColor.White : DrawingColor.FromArgb(32, 38, 49);
        _editor.CaretLineBackColor = darkTheme ? DrawingColor.FromArgb(70, 32, 39, 52) : DrawingColor.FromArgb(90, 224, 232, 245);
        _editor.SelectionBackColor = selectionBack;
        _editor.SelectionInactiveBackColor = inactiveSelectionBack;
        _editor.SelectionInactiveTextColor = editorFore;
        _editor.SelectionInactiveAdditionalBackColor = inactiveSelectionBack;
        _editor.SelectionInactiveAdditionalTextColor = editorFore;
        _editor.Margins[0].BackColor = marginBack;
        _editor.Styles[ScintillaNET.Style.LineNumber].BackColor = marginBack;
        _editor.Styles[ScintillaNET.Style.LineNumber].ForeColor = marginFore;
        _editor.Margins[0].Width = Math.Max(36, (int)Math.Round(44 * Math.Max(1f, dpiScale)));
        Background = new MediaBrush(MediaColor.FromRgb(editorBack.R, editorBack.G, editorBack.B));
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        ContentRevision++;
        RevisionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Editor_UpdateUI(object? sender, UpdateUIEventArgs e) => CaretChanged?.Invoke(this, EventArgs.Empty);

    private void Editor_SavePointChanged(object? sender, EventArgs e) => DirtyChanged?.Invoke(this, EventArgs.Empty);

    private bool ProcessShortcut(Forms.Keys keyData)
    {
        if (!TryGetShortcut(keyData, out var shortcut) || ShortcutRequested is not { } handler) return false;
        handler(this, new EditorShortcutEventArgs(shortcut));
        return true;
    }

    private static bool TryGetShortcut(Forms.Keys keyData, out EditorShortcut shortcut)
    {
        shortcut = default;
        var key = keyData & Forms.Keys.KeyCode;
        var modifiers = keyData & Forms.Keys.Modifiers;
        if (modifiers == Forms.Keys.Control)
        {
            shortcut = key switch
            {
                Forms.Keys.N => EditorShortcut.NewDocument,
                Forms.Keys.O => EditorShortcut.OpenDocument,
                Forms.Keys.S => EditorShortcut.SaveDocument,
                Forms.Keys.W or Forms.Keys.F4 => EditorShortcut.CloseDocument,
                Forms.Keys.F => EditorShortcut.Find,
                Forms.Keys.H => EditorShortcut.Replace,
                Forms.Keys.Tab or Forms.Keys.PageDown => EditorShortcut.NextDocument,
                Forms.Keys.PageUp => EditorShortcut.PreviousDocument,
                _ => default
            };
            return key is Forms.Keys.N or Forms.Keys.O or Forms.Keys.S or Forms.Keys.W or Forms.Keys.F4 or Forms.Keys.F or Forms.Keys.H or Forms.Keys.Tab or Forms.Keys.PageDown or Forms.Keys.PageUp;
        }
        if (modifiers == (Forms.Keys.Control | Forms.Keys.Shift))
        {
            shortcut = key switch
            {
                Forms.Keys.S => EditorShortcut.SaveDocumentAs,
                Forms.Keys.W => EditorShortcut.CloseAllDocuments,
                Forms.Keys.Tab => EditorShortcut.PreviousDocument,
                _ => default
            };
            return key is Forms.Keys.S or Forms.Keys.W or Forms.Keys.Tab;
        }
        if (modifiers == Forms.Keys.None && key == Forms.Keys.F3)
        {
            shortcut = EditorShortcut.FindNext;
            return true;
        }
        if (modifiers == Forms.Keys.Shift && key == Forms.Keys.F3)
        {
            shortcut = EditorShortcut.FindPrevious;
            return true;
        }
        return false;
    }

    private static void Editor_DragEnter(object? sender, Forms.DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(Forms.DataFormats.FileDrop) == true ? Forms.DragDropEffects.Copy : Forms.DragDropEffects.None;
    }

    private void Editor_DragDrop(object? sender, Forms.DragEventArgs e)
    {
        if (e.Data?.GetData(Forms.DataFormats.FileDrop) is string[] paths)
            FilesDropped?.Invoke(paths);
    }

    private sealed class ShortcutScintilla : Scintilla
    {
        private Func<Forms.Keys, bool>? _shortcutProcessor;

        public void SetShortcutProcessor(Func<Forms.Keys, bool>? shortcutProcessor) => _shortcutProcessor = shortcutProcessor;

        protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData) =>
            _shortcutProcessor?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);
    }
}

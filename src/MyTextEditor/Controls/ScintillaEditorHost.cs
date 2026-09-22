using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Windows.Forms.Integration;
using ScintillaNET;
using MyTextEditor.Models;
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
    FindPrevious,
    Help,
    DiffPrevious,
    DiffNext,
    MergeLeft,
    MergeRight,
    ZoomIn,
    ZoomOut,
    ZoomReset,
    GoToLine,
    CloseWindow,
    PreviousDifference = DiffPrevious,
    NextDifference = DiffNext,
    MergeRightToLeft = MergeLeft,
    MergeLeftToRight = MergeRight
}

public sealed class EditorShortcutEventArgs(EditorShortcut shortcut) : EventArgs
{
    public EditorShortcut Shortcut { get; } = shortcut;
    public bool Handled { get; set; }
}

public enum DiffHighlightKind
{
    Added,
    Deleted,
    Modified
}

public readonly record struct EditorTextRange(int Start, int Length)
{
    public int End => Start + Length;
}

public readonly record struct DiffLineHighlight(int StartLine, int LineCount, DiffHighlightKind Kind);

public readonly record struct DiffInlineHighlight(int Start, int Length, DiffHighlightKind Kind);

public sealed partial class ScintillaEditorHost : WindowsFormsHost
{
    private const int SciAddText = 2001;
    private const int SciClearAll = 2004;
    private const int SciSetUndoCollection = 2012;
    private const int SciSetCodePage = 2037;
    private const int SciAllocate = 2446;
    private const int Utf8CodePage = 65001;
    private const int AddedMarker = 20;
    private const int DeletedMarker = 21;
    private const int ModifiedMarker = 22;
    private const int AddedIndicator = 20;
    private const int DeletedIndicator = 21;
    private const int ModifiedIndicator = 22;
    private const int CurrentLineMarker = 24;
    private int _markedCurrentLine = -1;
    private int _lastNotifiedFirstVisibleLine = -1;
    private MarkerHandle? _currentLineMarkerHandle;

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
        _editor.Margins[1].Type = MarginType.Symbol;
        _editor.Margins[1].Mask = 1 << CurrentLineMarker;
        _editor.Margins[1].Width = 3;
        _editor.Markers[CurrentLineMarker].Symbol = MarkerSymbol.FullRect;
        _editor.TextChanged += Editor_TextChanged;
        _editor.UpdateUI += Editor_UpdateUI;
        _editor.SavePointLeft += Editor_SavePointChanged;
        _editor.SavePointReached += Editor_SavePointChanged;
        _editor.DragEnter += Editor_DragEnter;
        _editor.DragDrop += Editor_DragDrop;
        _editor.GotFocus += Editor_GotFocus;
        InitializeExtras();
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
    public bool IsReadOnly
    {
        get => _editor.ReadOnly;
        set => _editor.ReadOnly = value;
    }
    public bool HasSelection => _editor.SelectionStart != _editor.SelectionEnd;
    public bool IsEditorFocused => _editor.ContainsFocus;
    public string SelectedText => _editor.SelectedText;
    public EditorTextRange SelectionRange
    {
        get
        {
            var start = Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
            return new EditorTextRange(start, Math.Abs(_editor.SelectionEnd - _editor.SelectionStart));
        }
    }
    public int SelectionStartUtf16 => Math.Min(_editor.SelectionStart, _editor.SelectionEnd);
    public int SelectionEndUtf16 => Math.Max(_editor.SelectionStart, _editor.SelectionEnd);
    public void SelectUtf16Range(int start, int length, bool focusEditor = true)
    {
        SelectAndReveal(new EditorTextRange(start, length), focusEditor);
    }
    public bool FindOccurrence(string text, bool matchCase, bool wholeWord, bool previous, out bool wrapped)
    {
        wrapped = false;
        if (string.IsNullOrEmpty(text)) return false;
        var flags = ScintillaNET.SearchFlags.None;
        if (matchCase) flags |= ScintillaNET.SearchFlags.MatchCase;
        if (wholeWord) flags |= ScintillaNET.SearchFlags.WholeWord;
        var boundary = previous ? SelectionStartUtf16 : SelectionEndUtf16;
        var found = previous
            ? _editor.FindText(flags, text, boundary, 0)
            : _editor.FindText(flags, text, boundary, _editor.TextLength);
        if (found < 0)
        {
            wrapped = true;
            found = previous
                ? _editor.FindText(flags, text, _editor.TextLength, boundary)
                : _editor.FindText(flags, text, 0, boundary);
        }
        if (found < 0) return false;
        SelectAndReveal(new EditorTextRange(found, text.Length));
        return true;
    }
    public int LineCount => _editor.Lines.Count;
    public int CurrentLine => _editor.LineFromPosition(_editor.CurrentPosition);
    public int CurrentColumn => _editor.GetColumn(_editor.CurrentPosition);
    public int FirstVisibleLine => _editor.FirstVisibleLine;
    public int LinesOnScreen => _editor.LinesOnScreen;

    public event EventHandler? CaretChanged;
    public event EventHandler? EditorFocused;
    private void Editor_GotFocus(object? sender, EventArgs e) => EditorFocused?.Invoke(this, EventArgs.Empty);
    public event EventHandler? RevisionChanged;
    public event EventHandler? DirtyChanged;
    public event EventHandler? ViewportChanged;
    public event EventHandler? VerticalScrolled;
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
        _editor.GotFocus -= Editor_GotFocus;
        _editor.DragDrop -= Editor_DragDrop;
        ReleaseExtras();
        Child = null;
        _surface.Dispose();
        CaretChanged = null;
        RevisionChanged = null;
        DirtyChanged = null;
        ViewportChanged = null;
        VerticalScrolled = null;
        ShortcutRequested = null;
        FilesDropped = null;
    }

    public void ReloadUtf8(ReadOnlyMemory<byte> utf8)
    {
        var previousRevision = ContentRevision;
        LoadUtf8(utf8);
        ContentRevision = previousRevision + 1;
        RevisionChanged?.Invoke(this, EventArgs.Empty);
    }

    public unsafe void LoadUtf8(ReadOnlyMemory<byte> utf8)
    {
        _editor.CreateControl();
        ClearUserHighlights();
        _markedCurrentLine = -1;
        _lastNotifiedFirstVisibleLine = -1;
        _currentLineMarkerHandle = null;
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

    public string GetTextRange(EditorTextRange range)
    {
        ValidateRange(range);
        return _editor.GetTextRange(range.Start, range.Length);
    }

    public EditorTextRange GetLineTextRange(int zeroBasedLine, int utf16Start, int utf16Length)
    {
        if (zeroBasedLine < 0 || zeroBasedLine >= _editor.Lines.Count)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedLine));

        var line = _editor.Lines[zeroBasedLine];
        var lineText = line.Text;
        var contentLength = lineText.Length;
        if (contentLength > 0 && lineText[^1] == '\n')
        {
            contentLength--;
            if (contentLength > 0 && lineText[contentLength - 1] == '\r') contentLength--;
        }
        else if (contentLength > 0 && lineText[^1] == '\r')
        {
            contentLength--;
        }

        if (utf16Start < 0 || utf16Length < 0 || (long)utf16Start + utf16Length > contentLength)
            throw new ArgumentOutOfRangeException(nameof(utf16Start));
        if (!IsUtf16Boundary(lineText, utf16Start) || !IsUtf16Boundary(lineText, utf16Start + utf16Length))
            throw new ArgumentException("범위가 UTF-16 surrogate pair의 중간을 가리킵니다.");

        // ScintillaNET converts its public character positions to native UTF-8 byte offsets.
        return new EditorTextRange(line.Position + utf16Start, utf16Length);
    }

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

    public bool ReplaceRange(EditorTextRange range, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateRange(range);
        if (_editor.ReadOnly) return false;

        _editor.BeginUndoAction();
        try
        {
            _editor.TargetStart = range.Start;
            _editor.TargetEnd = range.End;
            _editor.ReplaceTarget(text);
        }
        finally
        {
            _editor.EndUndoAction();
        }
        return true;
    }

    public void SetSelection(EditorTextRange range)
    {
        ValidateRange(range);
        _editor.SetSelection(range.End, range.Start);
    }

    public void SetFirstVisibleLine(int zeroBasedLine)
    {
        if (_editor.Lines.Count == 0) return;
        var target = Math.Clamp(zeroBasedLine, 0, _editor.Lines.Count - 1);
        if (_editor.FirstVisibleLine != target) _editor.FirstVisibleLine = target;
        NotifyVerticalScroll();
    }

    public void ScrollToLine(int zeroBasedLine) => SetFirstVisibleLine(zeroBasedLine);

    public void SetDiffHighlights(
        IEnumerable<DiffLineHighlight> lineHighlights,
        IEnumerable<DiffInlineHighlight>? inlineHighlights = null)
    {
        ArgumentNullException.ThrowIfNull(lineHighlights);
        ClearDiffHighlights();

        foreach (var highlight in lineHighlights)
        {
            if (highlight.LineCount <= 0) continue;
            var start = Math.Clamp(highlight.StartLine, 0, _editor.Lines.Count);
            var end = Math.Clamp((long)highlight.StartLine + highlight.LineCount, 0, _editor.Lines.Count);
            var marker = GetMarker(highlight.Kind);
            for (var line = start; line < end; line++)
                _editor.Lines[line].MarkerAdd(marker);
        }

        if (inlineHighlights is null) return;
        foreach (var highlight in inlineHighlights)
        {
            if (highlight.Length <= 0) continue;
            ValidateRange(new EditorTextRange(highlight.Start, highlight.Length));
            _editor.IndicatorCurrent = GetIndicator(highlight.Kind);
            _editor.IndicatorFillRange(highlight.Start, highlight.Length);
        }
    }

    public void ClearDiffHighlights()
    {
        _editor.Markers[AddedMarker].DeleteAll();
        _editor.Markers[DeletedMarker].DeleteAll();
        _editor.Markers[ModifiedMarker].DeleteAll();
        foreach (var indicator in new[] { AddedIndicator, DeletedIndicator, ModifiedIndicator })
        {
            _editor.IndicatorCurrent = indicator;
            _editor.IndicatorClearRange(0, _editor.TextLength);
        }
    }

    public void GoToLine(int oneBasedLineNumber, bool focusEditor = true)
    {
        if (_editor.Lines.Count == 0) return;
        var index = Math.Clamp(oneBasedLineNumber - 1, 0, _editor.Lines.Count - 1);
        var line = _editor.Lines[index];
        _editor.SetSelection(line.EndPosition, line.Position);
        _editor.ScrollCaret();
        if (focusEditor) _editor.Focus();
    }

    public void Undo() => _editor.Undo();
    public void Redo() => _editor.Redo();
    public void SelectAll() => _editor.SelectAll();

    public void SelectAndReveal(EditorTextRange range, bool focusEditor = true)
    {
        ValidateRange(range);
        _editor.SetSelection(range.End, range.Start);
        _editor.ScrollCaret();
        if (focusEditor) _editor.Focus();
    }
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

    public void ApplyAppearance(string fontFamily, float fontSize, bool darkTheme, float dpiScale = 1f) =>
        ApplyAppearance(fontFamily, fontSize, ThemePalette.Get(darkTheme ? "Dark" : "Light"), dpiScale);

    private void ApplyFontAppearance(string fontFamily, float fontSize, bool darkTheme, float dpiScale)
    {
        var editorBack = darkTheme ? DrawingColor.FromArgb(25, 31, 41) : DrawingColor.White;
        var editorFore = darkTheme ? DrawingColor.FromArgb(236, 240, 246) : DrawingColor.FromArgb(32, 38, 49);
        var marginBack = darkTheme ? DrawingColor.FromArgb(17, 21, 28) : DrawingColor.FromArgb(247, 249, 252);
        var marginFore = darkTheme ? DrawingColor.FromArgb(163, 174, 194) : DrawingColor.FromArgb(102, 112, 133);
        var selectionBack = darkTheme ? DrawingColor.FromArgb(41, 70, 119) : DrawingColor.FromArgb(220, 232, 255);
        var inactiveSelectionBack = darkTheme ? DrawingColor.FromArgb(48, 58, 74) : DrawingColor.FromArgb(226, 230, 237);
        var style = _editor.Styles[ScintillaNET.Style.Default];
        style.Font = fontFamily;
        style.SizeF = Math.Clamp(fontSize, 6f, 72f);
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
        ConfigureDiffMarkers(darkTheme);
        Background = new MediaBrush(MediaColor.FromRgb(editorBack.R, editorBack.G, editorBack.B));
    }

    public void ApplyAppearance(string fontFamily, float fontSize, ThemePalette palette, float dpiScale = 1f)
    {
        ApplyFontAppearance(fontFamily, fontSize, palette.IsDark, dpiScale);
        _editor.Styles[ScintillaNET.Style.Default].ForeColor = palette.EditorForeground;
        _editor.Styles[ScintillaNET.Style.Default].BackColor = palette.EditorBackground;
        _editor.StyleClearAll();
        _editor.BackColor = palette.EditorBackground;
        _editor.ForeColor = palette.EditorForeground;
        _editor.CaretForeColor = palette.EditorForeground;
        _editor.CaretLineBackColor = palette.CurrentLine;
        _editor.CaretLineLayer = Layer.Base;
        _editor.CaretLineVisibleAlways = true;
        _editor.SelectionBackColor = palette.SelectionBackground;
        _editor.SelectionTextColor = palette.SelectionForeground;
        _editor.SelectionAdditionalBackColor = palette.SelectionBackground;
        _editor.SelectionAdditionalTextColor = palette.SelectionForeground;
        _editor.SelectionInactiveBackColor = palette.InactiveSelection;
        _editor.SelectionInactiveTextColor = palette.InactiveSelectionForeground;
        _editor.SelectionInactiveAdditionalBackColor = palette.InactiveSelection;
        _editor.SelectionInactiveAdditionalTextColor = palette.InactiveSelectionForeground;
        _editor.Markers[CurrentLineMarker].SetBackColor(palette.Accent);
        _editor.Margins[1].Width = Math.Max(3, (int)(3 * dpiScale));
        _editor.Margins[0].BackColor = palette.MarginBackground;
        _editor.Styles[ScintillaNET.Style.LineNumber].BackColor = palette.MarginBackground;
        _editor.Styles[ScintillaNET.Style.LineNumber].ForeColor = palette.MarginForeground;
        _surface.BackColor = palette.EditorBackground;
        _verticalBar.Width = Math.Max(14, (int)(14 * dpiScale));
        _horizontalBar.Height = Math.Max(14, (int)(14 * dpiScale));
        _verticalBar.ApplyPalette(palette);
        _horizontalBar.ApplyPalette(palette);
        _contextMenu.BackColor = palette.MarginBackground;
        _contextMenu.ForeColor = palette.EditorForeground;
        var menuRenderer = new EditorMenuRenderer(palette);
        ApplyMenuPalette(_contextMenu);
        void ApplyMenuPalette(Forms.ToolStrip strip)
        {
            strip.Renderer = menuRenderer;
            strip.BackColor = palette.MarginBackground;
            strip.ForeColor = palette.EditorForeground;
            foreach (Forms.ToolStripItem item in strip.Items)
            {
                item.ForeColor = palette.EditorForeground;
                if (item is Forms.ToolStripMenuItem menu && menu.HasDropDownItems) ApplyMenuPalette(menu.DropDown);
            }
        }
        Background = new MediaBrush(MediaColor.FromRgb(palette.EditorBackground.R, palette.EditorBackground.G, palette.EditorBackground.B));
        UpdateScrollBars();
    }

    private void ConfigureDiffMarkers(bool darkTheme)
    {
        ConfigureMarker(AddedMarker, darkTheme ? DrawingColor.FromArgb(31, 66, 49) : DrawingColor.FromArgb(222, 247, 230));
        ConfigureMarker(DeletedMarker, darkTheme ? DrawingColor.FromArgb(74, 39, 43) : DrawingColor.FromArgb(255, 230, 230));
        ConfigureMarker(ModifiedMarker, darkTheme ? DrawingColor.FromArgb(67, 58, 31) : DrawingColor.FromArgb(255, 245, 204));
        ConfigureIndicator(AddedIndicator, darkTheme ? DrawingColor.FromArgb(46, 160, 87) : DrawingColor.FromArgb(64, 160, 91));
        ConfigureIndicator(DeletedIndicator, darkTheme ? DrawingColor.FromArgb(218, 84, 89) : DrawingColor.FromArgb(210, 63, 68));
        ConfigureIndicator(ModifiedIndicator, darkTheme ? DrawingColor.FromArgb(218, 176, 66) : DrawingColor.FromArgb(205, 151, 31));
    }

    private void ConfigureMarker(int index, DrawingColor color)
    {
        var marker = _editor.Markers[index];
        marker.Symbol = MarkerSymbol.Background;
        marker.SetBackColor(color);
    }

    private void ConfigureIndicator(int index, DrawingColor color)
    {
        var indicator = _editor.Indicators[index];
        indicator.Style = IndicatorStyle.FullBox;
        indicator.ForeColor = color;
        indicator.Alpha = 80;
        indicator.OutlineAlpha = 110;
        indicator.Under = true;
    }

    private static int GetMarker(DiffHighlightKind kind) => kind switch
    {
        DiffHighlightKind.Added => AddedMarker,
        DiffHighlightKind.Deleted => DeletedMarker,
        _ => ModifiedMarker
    };

    private static int GetIndicator(DiffHighlightKind kind) => kind switch
    {
        DiffHighlightKind.Added => AddedIndicator,
        DiffHighlightKind.Deleted => DeletedIndicator,
        _ => ModifiedIndicator
    };

    private void ValidateRange(EditorTextRange range)
    {
        if (range.Start < 0 || range.Length < 0 || range.End > _editor.TextLength)
            throw new ArgumentOutOfRangeException(nameof(range));
    }

    private static bool IsUtf16Boundary(string text, int index) =>
        index <= 0 || index >= text.Length ||
        !(char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]));

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        ContentRevision++;
        ScheduleHighlightRefresh();
        RevisionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Editor_UpdateUI(object? sender, UpdateUIEventArgs e)
    {
        CaretChanged?.Invoke(this, EventArgs.Empty);
        if (_markedCurrentLine != CurrentLine)
        {
            if (_currentLineMarkerHandle is { } handle) _editor.MarkerDeleteHandle(handle);
            _currentLineMarkerHandle = _editor.Lines[CurrentLine].MarkerAdd(CurrentLineMarker);
            _markedCurrentLine = CurrentLine;
        }
        UpdateScrollBars();
        if ((e.Change & UpdateChange.VScroll) != 0)
        {
            NotifyVerticalScroll();
        }
    }

    private void NotifyVerticalScroll()
    {
        if (_loading || _resourcesReleased) return;
        var actual = _editor.FirstVisibleLine;
        if (_lastNotifiedFirstVisibleLine == actual) return;
        // Cache before notifying: the opposite Diff pane can synchronously scroll in its handler.
        _lastNotifiedFirstVisibleLine = actual;
        UpdateScrollBars();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        VerticalScrolled?.Invoke(this, EventArgs.Empty);
    }

    private void Editor_SavePointChanged(object? sender, EventArgs e) => DirtyChanged?.Invoke(this, EventArgs.Empty);

    private bool ProcessShortcut(Forms.Keys keyData)
    {
        if (keyData == Forms.Keys.Escape && (_editor.IsComposing || _contextMenu.Visible)) return false;
        if (!TryGetShortcut(keyData, out var shortcut) || ShortcutRequested is not { } handler) return false;
        var args = new EditorShortcutEventArgs(shortcut);
        handler(this, args);
        return args.Handled;
    }

    private static bool TryGetShortcut(Forms.Keys keyData, out EditorShortcut shortcut)
    {
        shortcut = default;
        var key = keyData & Forms.Keys.KeyCode;
        var modifiers = keyData & Forms.Keys.Modifiers;
        if (modifiers == Forms.Keys.None && key == Forms.Keys.Escape)
        {
            shortcut = EditorShortcut.CloseWindow;
            return true;
        }
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
                Forms.Keys.G => EditorShortcut.GoToLine,
                Forms.Keys.Oemplus or Forms.Keys.Add => EditorShortcut.ZoomIn,
                Forms.Keys.OemMinus or Forms.Keys.Subtract => EditorShortcut.ZoomOut,
                Forms.Keys.D0 or Forms.Keys.NumPad0 => EditorShortcut.ZoomReset,
                Forms.Keys.Tab or Forms.Keys.PageDown => EditorShortcut.NextDocument,
                Forms.Keys.PageUp => EditorShortcut.PreviousDocument,
                _ => default
            };
            return key is Forms.Keys.N or Forms.Keys.O or Forms.Keys.S or Forms.Keys.W or Forms.Keys.F4 or Forms.Keys.F or Forms.Keys.H or Forms.Keys.G or Forms.Keys.Oemplus or Forms.Keys.Add or Forms.Keys.OemMinus or Forms.Keys.Subtract or Forms.Keys.D0 or Forms.Keys.NumPad0 or Forms.Keys.Tab or Forms.Keys.PageDown or Forms.Keys.PageUp;
        }
        if (modifiers == (Forms.Keys.Control | Forms.Keys.Shift))
        {
            shortcut = key switch
            {
                Forms.Keys.S => EditorShortcut.SaveDocumentAs,
                Forms.Keys.W => EditorShortcut.CloseAllDocuments,
                Forms.Keys.Tab => EditorShortcut.PreviousDocument,
                Forms.Keys.Oemplus => EditorShortcut.ZoomIn,
                _ => default
            };
            return key is Forms.Keys.S or Forms.Keys.W or Forms.Keys.Tab or Forms.Keys.Oemplus;
        }
        if (modifiers == Forms.Keys.None && key == Forms.Keys.F3)
        {
            shortcut = EditorShortcut.FindNext;
            return true;
        }
        if (modifiers == Forms.Keys.None && key == Forms.Keys.F1)
        {
            shortcut = EditorShortcut.Help;
            return true;
        }
        if (modifiers == Forms.Keys.None && key is Forms.Keys.F7 or Forms.Keys.F8)
        {
            shortcut = key == Forms.Keys.F7 ? EditorShortcut.DiffPrevious : EditorShortcut.DiffNext;
            return true;
        }
        if (modifiers == Forms.Keys.Shift && key == Forms.Keys.F3)
        {
            shortcut = EditorShortcut.FindPrevious;
            return true;
        }
        if (modifiers == Forms.Keys.Alt)
        {
            shortcut = key switch
            {
                Forms.Keys.Up => EditorShortcut.DiffPrevious,
                Forms.Keys.Down => EditorShortcut.DiffNext,
                Forms.Keys.Left => EditorShortcut.MergeLeft,
                Forms.Keys.Right => EditorShortcut.MergeRight,
                _ => default
            };
            return key is Forms.Keys.Up or Forms.Keys.Down or Forms.Keys.Left or Forms.Keys.Right;
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
        public bool IsComposing { get; private set; }

        public void SetShortcutProcessor(Func<Forms.Keys, bool>? shortcutProcessor) => _shortcutProcessor = shortcutProcessor;

        protected override bool ProcessCmdKey(ref Forms.Message msg, Forms.Keys keyData) =>
            _shortcutProcessor?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == 0x010D) IsComposing = true; // WM_IME_STARTCOMPOSITION
            if (message.Msg == 0x010E) IsComposing = false; // WM_IME_ENDCOMPOSITION
            const int mouseWheel = 0x020A;
            if (message.Msg == mouseWheel && (Forms.Control.ModifierKeys & Forms.Keys.Control) != 0)
            {
                var delta = unchecked((short)((long)message.WParam >> 16));
                _shortcutProcessor?.Invoke(Forms.Keys.Control | (delta > 0 ? Forms.Keys.Add : Forms.Keys.Subtract));
                return;
            }
            base.WndProc(ref message);
        }
    }
}

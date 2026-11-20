using System.Text;
using ScintillaNET;

namespace MyTextEditor.Controls;

public sealed partial class ScintillaEditorHost
{
    private const int OccurrenceIndicator = 24;
    private bool _occurrencesEnabled;
    private bool _hasOccurrenceMarks;
    private OccurrenceView? _occurrenceView;
    private readonly record struct OccurrenceView(int Start, int End, bool WholeWord, long Revision,
        int FirstLine, int HorizontalOffset, int Width, int Height, int LineHeight, bool Wrap);

    public void EnableSelectionOccurrences()
    {
        if (_occurrencesEnabled) return;
        _occurrencesEnabled = true;
        _editor.DoubleClick += (_, _) => _editor.DoubleClickSelection = true;
        _editor.SizeChanged += (_, _) => RefreshSelectionOccurrences();
    }

    private int NativePosition(uint message, int first = 0, int second = 0) =>
        (int)_editor.DirectMessage((int)message, (IntPtr)first, (IntPtr)second);

    private unsafe byte[] ReadOccurrenceBytes(int start, int length)
    {
        var address = _editor.DirectMessage((int)SciApi.SCI_GETRANGEPOINTER, (IntPtr)start, (IntPtr)length);
        return new ReadOnlySpan<byte>((void*)address, length).ToArray();
    }

    private void RefreshSelectionOccurrences()
    {
        if (!_occurrencesEnabled || _loading || _resourcesReleased) return;
        var start = NativePosition(SciApi.SCI_GETSELECTIONSTART);
        var end = NativePosition(SciApi.SCI_GETSELECTIONEND);
        if (start == end && !_hasOccurrenceMarks)
        {
            _occurrenceView = null;
            return;
        }

        var size = _editor.ClientSize;
        var rowHeight = Math.Max(1, NativePosition(SciApi.SCI_TEXTHEIGHT));
        var view = new OccurrenceView(start, end, _editor.DoubleClickSelection, ContentRevision,
            FirstVisibleLine, HorizontalOffset, size.Width, size.Height, rowHeight, WordWrap);
        if (_occurrenceView == view) return;
        _occurrenceView = view;
        var documentLength = Utf8ByteLength;
        _editor.IndicatorCurrent = OccurrenceIndicator;
        if (_hasOccurrenceMarks)
        {
            _editor.DirectMessage(SciIndicatorClearRange, IntPtr.Zero, (IntPtr)documentLength);
            _hasOccurrenceMarks = false;
        }
        var length = end - start;
        if (length == 0 || size.Width <= 0 || size.Height <= 0 ||
            length > Math.Max(start, documentLength - end) ||
            NativePosition(SciApi.SCI_LINEFROMPOSITION, start) != NativePosition(SciApi.SCI_LINEFROMPOSITION, end))
            return;
        var phrase = ReadOccurrenceBytes(start, length);
        if (phrase.AsSpan().IndexOfAny((byte)'\r', (byte)'\n') >= 0 || IsWhiteSpace(phrase)) return;

        // Limit each row horizontally as well, since a single log line can span many megabytes.
        var lastLeft = -1;
        var lastRight = -1;
        for (var y = 0; y < size.Height; y += rowHeight)
        {
            var left = NativePosition(SciApi.SCI_POSITIONFROMPOINT, 0, y);
            var right = NativePosition(SciApi.SCI_POSITIONFROMPOINT, size.Width - 1, y);
            if (left < 0 || right < left || (left == lastLeft && right == lastRight)) continue;
            lastLeft = left;
            lastRight = right;
            var line = NativePosition(SciApi.SCI_LINEFROMPOSITION, left);
            var rowStart = Math.Max(NativePosition(SciApi.SCI_POSITIONFROMLINE, line), left - length + 1);
            var rowEnd = (int)Math.Min(NativePosition(SciApi.SCI_GETLINEENDPOSITION, line), (long)right + length);
            MarkOccurrenceRange(rowStart, Math.Min(rowEnd, start), phrase, view.WholeWord);
            MarkOccurrenceRange(Math.Max(rowStart, end), rowEnd, phrase, view.WholeWord);
        }
    }

    private void MarkOccurrenceRange(int start, int end, byte[] phrase, bool wholeWord)
    {
        if (end - start < phrase.Length) return;
        var bytes = ReadOccurrenceBytes(start, end - start);
        for (var offset = 0; offset <= bytes.Length - phrase.Length; )
        {
            var match = bytes.AsSpan(offset).IndexOf(phrase);
            if (match < 0) break;
            offset += match;
            var position = start + offset;
            if (!wholeWord || NativePosition(SciApi.SCI_ISRANGEWORD, position, position + phrase.Length) != 0)
            {
                _editor.DirectMessage((int)SciApi.SCI_INDICATORFILLRANGE, (IntPtr)position, (IntPtr)phrase.Length);
                _hasOccurrenceMarks = true;
            }
            offset++;
        }
    }

    private static bool IsWhiteSpace(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            Rune.DecodeFromUtf8(bytes, out var rune, out var consumed);
            if (!Rune.IsWhiteSpace(rune)) return false;
            bytes = bytes[consumed..];
        }
        return true;
    }
}

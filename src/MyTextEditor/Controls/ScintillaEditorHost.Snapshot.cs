using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MyTextEditor.Controls;

public sealed partial class ScintillaEditorHost
{
    public int Utf8ByteLength => checked((int)_editor.DirectMessage(2006, IntPtr.Zero, IntPtr.Zero));

    public async Task<string> CaptureTextAsync(CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        var revision = ContentRevision;
        var bytes = new byte[Utf8ByteLength];
        for (var offset = 0; offset < bytes.Length; offset += 65536)
        {
            token.ThrowIfCancellationRequested();
            if (_resourcesReleased || revision != ContentRevision) throw new OperationCanceledException("문서가 변경되었습니다.");
            var length = Math.Min(65536, bytes.Length - offset);
            var pointer = _editor.DirectMessage(2643, (IntPtr)offset, (IntPtr)length);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("편집기 내용을 읽을 수 없습니다.");
            Marshal.Copy(pointer, bytes, offset, length);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        var text = await Task.Run(() => Encoding.UTF8.GetString(bytes), token);
        if (_resourcesReleased || revision != ContentRevision) throw new OperationCanceledException("문서가 변경되었습니다.");
        return text;
    }
}

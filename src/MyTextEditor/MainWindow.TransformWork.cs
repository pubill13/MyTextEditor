using MyTextEditor.Core.Models;

namespace MyTextEditor;

public partial class MainWindow
{
    private bool _transformRunning;

    private async Task RunTransformAsync(Func<string, TextTransformResult> transform, string title, bool preview)
    {
        if (CurrentDocument is not { } document || _closingInProgress) return;
        if (_transformRunning)
        {
            StatusMessage.Text = "진행 중인 텍스트 정리가 끝난 뒤 다시 실행하세요.";
            return;
        }
        _transformRunning = true;
        var revision = document.ContentRevision;
        try
        {
            StatusMessage.Text = $"{title}: 처리 중…";
            var text = document.Text;
            var result = text.Length < 1_000_000 ? transform(text) : await Task.Run(() => transform(text));
            // A background result must never replace a different tab or newer user edits.
            if (_closingInProgress) return;
            if (!Documents.Contains(document) || CurrentDocument != document || document.ContentRevision != revision)
            {
                StatusMessage.Text = "문서가 변경되거나 탭이 전환되어 정리 결과를 적용하지 않았습니다. 다시 실행하세요.";
                return;
            }
            if (preview) ShowTransformPreview(result, title);
            else ApplyTransformDirect(result, title);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusMessage.Text = $"{title}: {exception.Message}";
        }
        finally { _transformRunning = false; }
    }
}

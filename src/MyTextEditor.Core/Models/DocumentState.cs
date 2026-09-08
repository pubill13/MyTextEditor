using System.Text;

namespace MyTextEditor.Core.Models;

public sealed class DocumentState
{
    public string? FilePath { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsModified { get; set; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false, true);
    public bool HasByteOrderMark { get; set; }
    public string NewLine { get; set; } = "\r\n";

    public string DisplayName => FilePath is null ? "새 문서" : Path.GetFileName(FilePath);
}

using System.Text;

namespace MyTextEditor.Core;

public enum DocumentEncodingKind { Utf8, Utf8Bom, Utf16Le, Utf16Be, Windows949 }

public static class DocumentEncodings
{
    static DocumentEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding GetEncoding(DocumentEncodingKind kind) => kind switch
    {
        DocumentEncodingKind.Utf8 => new UTF8Encoding(false, true),
        DocumentEncodingKind.Utf8Bom => new UTF8Encoding(true, true),
        DocumentEncodingKind.Utf16Le => new UnicodeEncoding(false, true, true),
        DocumentEncodingKind.Utf16Be => new UnicodeEncoding(true, true, true),
        DocumentEncodingKind.Windows949 => Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool HasBom(DocumentEncodingKind kind) => kind is DocumentEncodingKind.Utf8Bom or DocumentEncodingKind.Utf16Le or DocumentEncodingKind.Utf16Be;

    public static string GetDisplayName(DocumentEncodingKind kind) => kind switch
    {
        DocumentEncodingKind.Utf8 => "UTF-8",
        DocumentEncodingKind.Utf8Bom => "UTF-8 BOM",
        DocumentEncodingKind.Utf16Le => "UTF-16 LE BOM",
        DocumentEncodingKind.Utf16Be => "UTF-16 BE BOM",
        DocumentEncodingKind.Windows949 => "한국어 Windows-949",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static DocumentEncodingKind FromEncoding(Encoding encoding, bool hasBom) => encoding.CodePage switch
    {
        65001 => hasBom ? DocumentEncodingKind.Utf8Bom : DocumentEncodingKind.Utf8,
        1200 => DocumentEncodingKind.Utf16Le,
        1201 => DocumentEncodingKind.Utf16Be,
        949 => DocumentEncodingKind.Windows949,
        _ => throw new ArgumentException("지원하지 않는 인코딩입니다.", nameof(encoding))
    };
}

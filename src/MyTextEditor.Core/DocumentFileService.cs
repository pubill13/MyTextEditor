using System.Text;
using MyTextEditor.Core.Models;

namespace MyTextEditor.Core;

public sealed class DocumentFileService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static DocumentFileService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<DocumentState> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var detected = DetectEncoding(bytes);
        var preambleLength = detected.HasBom ? detected.Encoding.GetPreamble().Length : 0;
        var text = detected.Encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);

        return new DocumentState
        {
            FilePath = Path.GetFullPath(filePath),
            Text = text,
            IsModified = false,
            Encoding = detected.Encoding,
            HasByteOrderMark = detected.HasBom,
            NewLine = TextLines.DetectNewLine(text)
        };
    }

    public async Task SaveAsync(DocumentState document, string? filePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var destination = filePath ?? document.FilePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var encoding = CreateStrictEncoding(document.Encoding, document.HasByteOrderMark);
        byte[] content;
        try
        {
            var normalizedText = NormalizeNewLines(document.Text, document.NewLine);
            var body = encoding.GetBytes(normalizedText);
            var preamble = document.HasByteOrderMark ? encoding.GetPreamble() : [];
            content = new byte[preamble.Length + body.Length];
            preamble.CopyTo(content, 0);
            body.CopyTo(content, preamble.Length);
        }
        catch (EncoderFallbackException exception)
        {
            throw new DocumentEncodingException(
                $"현재 인코딩({document.Encoding.EncodingName})으로 표현할 수 없는 문자가 있습니다.", exception);
        }

        var fullDestination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException("저장할 폴더를 확인할 수 없습니다.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullDestination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // The original exception is more useful than a temporary-file cleanup failure.
                }
            }
        }

        document.FilePath = fullDestination;
        document.IsModified = false;
    }

    public static (Encoding Encoding, bool HasBom) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.GetPreamble()))
            return (new UTF8Encoding(true, true), true);
        if (bytes.StartsWith(Encoding.Unicode.GetPreamble()))
            return (new UnicodeEncoding(false, true, true), true);
        if (bytes.StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return (new UnicodeEncoding(true, true, true), true);

        try
        {
            StrictUtf8.GetString(bytes);
            return (new UTF8Encoding(false, true), false);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(949,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback), false);
        }
    }

    private static Encoding CreateStrictEncoding(Encoding source, bool emitBom) => source.CodePage switch
    {
        65001 => new UTF8Encoding(emitBom, true),
        1200 => new UnicodeEncoding(false, emitBom, true),
        1201 => new UnicodeEncoding(true, emitBom, true),
        _ => Encoding.GetEncoding(source.CodePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback)
    };

    private static string NormalizeNewLines(string text, string newLine)
    {
        if (newLine is not ("\r\n" or "\n" or "\r"))
            throw new ArgumentException("지원하지 않는 줄바꿈 형식입니다.", nameof(newLine));

        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                builder.Append(newLine);
            }
            else if (text[index] == '\n')
            {
                builder.Append(newLine);
            }
            else
            {
                builder.Append(text[index]);
            }
        }
        return builder.ToString();
    }
}

public sealed class DocumentEncodingException : Exception
{
    public DocumentEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

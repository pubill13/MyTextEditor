using System.Text;
using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class ExplicitEncodingTests
{
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniEdit-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new DocumentFileService();
            var path = Path.Combine(directory, "한글 encoding.txt");
            foreach (var kind in Enum.GetValues<DocumentEncodingKind>())
            foreach (var newline in new[] { "\r\n", "\n", "\r" })
            {
                var text = "한글 English" + (kind == DocumentEncodingKind.Windows949 ? "" : " 😀") + newline + "끝";
                var document = new DocumentState
                {
                    Text = text, NewLine = newline, Encoding = DocumentEncodings.GetEncoding(kind),
                    HasByteOrderMark = DocumentEncodings.HasBom(kind)
                };
                await service.SaveAsync(document, path);
                var bytes = await File.ReadAllBytesAsync(path);
                var expected = document.Encoding.GetBytes(text);
                var bom = document.HasByteOrderMark ? document.Encoding.GetPreamble() : [];
                Assert.SequenceEqual(bom.Concat(expected), bytes);
                var loaded = await service.LoadAsync(path, kind);
                var buffer = await service.LoadBufferAsync(path, kind);
                Assert.Equal(text, loaded.Text);
                Assert.Equal(text, Encoding.UTF8.GetString(buffer.Utf8Buffer.Span));
                Assert.Equal(newline, buffer.NewLine);
                Assert.Equal(kind, DocumentEncodings.FromEncoding(buffer.OriginalEncoding, buffer.HasByteOrderMark));
                Assert.True(buffer.ReadElapsed >= TimeSpan.Zero && buffer.DecodeElapsed >= TimeSpan.Zero);
            }

            // Explicit selection must override valid UTF-8 auto-detection (ASCII is also valid UTF-16).
            await File.WriteAllBytesAsync(path, [0x41, 0x00]);
            Assert.Equal("A", (await service.LoadAsync(path, DocumentEncodingKind.Utf16Le)).Text);
            Assert.Equal("A", Encoding.UTF8.GetString((await service.LoadBufferAsync(path, DocumentEncodingKind.Utf16Le)).Utf8Buffer.Span));
            await File.WriteAllBytesAsync(path, [0xFF]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadAsync(path, DocumentEncodingKind.Utf8));
            await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadBufferAsync(path, DocumentEncodingKind.Utf8Bom));
            await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadAsync(path, DocumentEncodingKind.Utf16Le));
            await File.WriteAllBytesAsync(path, [0x81]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => service.LoadBufferAsync(path, DocumentEncodingKind.Windows949));

            // UTF-8 BOM is removed when reopening into UTF-8 without BOM.
            await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, 0x41]);
            var utf8 = await service.LoadAsync(path, DocumentEncodingKind.Utf8);
            Assert.Equal("A", utf8.Text);
            Assert.False(utf8.HasByteOrderMark);
            // A nonmatching BOM is not silently used to change the requested byte order.
            await File.WriteAllBytesAsync(path, [0xFE, 0xFF, 0x00, 0x41]);
            var wrongOrder = await service.LoadAsync(path, DocumentEncodingKind.Utf16Le);
            Assert.Equal("\uFFFE\u4100", wrongOrder.Text);

            var previous = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<DocumentEncodingException>(() => service.SaveAsync(new DocumentState
            {
                Text = "한글 😀", Encoding = DocumentEncodings.GetEncoding(DocumentEncodingKind.Windows949)
            }, path));
            Assert.SequenceEqual(previous, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

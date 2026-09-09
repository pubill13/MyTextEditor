namespace MyTextEditor.Core;

internal static class TextLines
{
    public static bool EndsWithNewLine(string text) => text.EndsWith('\r') || text.EndsWith('\n');

    public static IReadOnlyList<string> Split(string text)
    {
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            lines.Add(text[start..index]);
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        lines.Add(text[start..]);
        return lines;
    }

    public static string DetectNewLine(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[index] == '\n')
            {
                lf++;
            }
        }

        if (crlf >= lf && crlf >= cr && crlf > 0) return "\r\n";
        if (lf >= cr && lf > 0) return "\n";
        if (cr > 0) return "\r";
        return "\r\n";
    }

    public static string DetectNewLine(ReadOnlySpan<byte> utf8Bytes)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var index = 0; index < utf8Bytes.Length; index++)
        {
            if (utf8Bytes[index] == (byte)'\r')
            {
                if (index + 1 < utf8Bytes.Length && utf8Bytes[index + 1] == (byte)'\n')
                {
                    crlf++;
                    index++;
                }
                else
                {
                    cr++;
                }
            }
            else if (utf8Bytes[index] == (byte)'\n')
            {
                lf++;
            }
        }

        if (crlf >= lf && crlf >= cr && crlf > 0) return "\r\n";
        if (lf >= cr && lf > 0) return "\n";
        if (cr > 0) return "\r";
        return "\r\n";
    }
}

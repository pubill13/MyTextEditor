using MyTextEditor.Core;
using MyTextEditor.Core.Models;

internal static class LogCleanupOptionsTests
{
    public static Task IndependentOptions()
    {
        var service = new TextTransformService();
        const string text = "\u001b[31m한글😀\u001b[0m\0  \n\n\n끝\t";
        var ansiOnly = service.CleanupLog(text, new(true, false, false), "\n");
        Assert.Equal("한글😀  \n\n\n끝\t", ansiOnly.TransformResult.Text);
        Assert.Equal(2, ansiOnly.CleanupSummary.AnsiSequencesRemoved);
        Assert.Equal(1, ansiOnly.CleanupSummary.ControlCharactersRemoved);
        Assert.Equal(0, ansiOnly.CleanupSummary.TrailingWhitespaceCharactersRemoved);
        Assert.Equal(0, ansiOnly.CleanupSummary.CollapsedBlankLines);

        var trailingOnly = service.CleanupLog(text, new(false, true, false), "\n");
        Assert.Equal("\u001b[31m한글😀\u001b[0m\0\n\n\n끝", trailingOnly.TransformResult.Text);
        Assert.Equal(0, trailingOnly.CleanupSummary.AnsiSequencesRemoved);
        Assert.Equal(0, trailingOnly.CleanupSummary.ControlCharactersRemoved);
        Assert.Equal(3, trailingOnly.CleanupSummary.TrailingWhitespaceCharactersRemoved);

        var blankOnly = service.CleanupLog(text, new(false, false, true), "\n");
        Assert.Equal("\u001b[31m한글😀\u001b[0m\0  \n\n끝\t", blankOnly.TransformResult.Text);
        Assert.Equal(1, blankOnly.CleanupSummary.CollapsedBlankLines);
        Assert.Equal("first\n \t\nlast", service.CleanupLog("first\n \t\n\nlast", new(false, false, true), "\n").TransformResult.Text);

        foreach (var newline in new[] { "\r\n", "\n", "\r" })
        {
            var sample = text.Replace("\n", newline) + newline;
            Assert.Equal(service.CleanupLog(sample, newline).TransformResult.Text,
                service.CleanupLog(sample, new LogCleanupOptions(), newline).TransformResult.Text);
            var disabled = service.CleanupLog(sample, new(false, false, false), newline);
            Assert.Equal(sample, disabled.TransformResult.Text);
            Assert.Equal(0, disabled.TransformResult.Summary.ChangedLines);
        }
        const string mixed = "a \r\nb\n\n\rc\0\t";
        Assert.Equal(mixed, service.CleanupLog(mixed, new LogCleanupOptions(false, false, false)).TransformResult.Text);
        Assert.Equal(string.Empty, service.CleanupLog(string.Empty, new LogCleanupOptions(false, false, false)).TransformResult.Text);
        return Task.CompletedTask;
    }
}

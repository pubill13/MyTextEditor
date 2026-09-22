using MyTextEditor.Core.Models;

namespace MyTextEditor.Models;

public sealed record LogCleanupPreferences
{
    public bool RemoveAnsiAndControlCharacters { get; init; } = true;
    public bool TrimTrailingWhitespace { get; init; } = true;
    public bool CollapseBlankLines { get; init; } = true;

    public LogCleanupOptions ToOptions() => new(RemoveAnsiAndControlCharacters,
        TrimTrailingWhitespace, CollapseBlankLines);
}

namespace MyTextEditor.Help;

public sealed record HelpSection(string Heading, string Body, string? Example = null);

public sealed record HelpTopic(
    string Title,
    string Summary,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<HelpSection> Sections)
{
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return SearchableText.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private string SearchableText => string.Join('\n',
        new[] { Title, Summary }
            .Concat(Keywords)
            .Concat(Sections.SelectMany(section => new[] { section.Heading, section.Body, section.Example ?? string.Empty })));
}

public sealed record HelpWindowPlacement(double Width, double Height, double? Left = null, double? Top = null);

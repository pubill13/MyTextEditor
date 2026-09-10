namespace MyTextEditor.Help;

public static class HelpCatalog
{
    private static readonly Lazy<IReadOnlyList<HelpTopic>> DefaultTopics = new(HelpWindow.CreateTopics);

    public static IReadOnlyList<HelpTopic> Topics => DefaultTopics.Value;

    public static IReadOnlyList<HelpTopic> Search(IEnumerable<HelpTopic> topics, string? query)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return topics.Where(topic => topic.Matches(query ?? string.Empty)).ToArray();
    }
}

namespace MyTextEditor.Help;

public static class HelpCatalog
{
    private static readonly Lazy<IReadOnlyList<HelpTopic>> DefaultTopics = new(HelpWindow.CreateTopics);

    public static IReadOnlyList<HelpTopic> Topics => DefaultTopics.Value;

    public static IReadOnlyList<string> Categories { get; } =
    ["시작하기", "편집·표시", "검색·결과", "텍스트 정리", "매크로", "비교·병합", "설정·단축키·문제 해결"];

    public static IReadOnlyList<HelpTopic> Search(IEnumerable<HelpTopic> topics, string? query)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return topics.Where(topic => topic.Matches(query ?? string.Empty)).ToArray();
    }
}

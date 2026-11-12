using System.IO;

namespace MyTextEditor.Models;

public static class ComparisonPathHistory
{
    public const int MaximumCount = 30;

    public static List<string> Normalize(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        foreach (var value in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim().Trim('"')));
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Invalid entries from older or manually edited settings cannot be reused.
            }
            if (result.Count == MaximumCount) break;
        }
        return result;
    }

    public static List<string> Remember(IEnumerable<string> recent, IEnumerable<string> paths) =>
        Normalize(paths.Concat(recent));
}

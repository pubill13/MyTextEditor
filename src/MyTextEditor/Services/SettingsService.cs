using System.IO;
using System.Text.Json;
using MyTextEditor.Models;

namespace MyTextEditor.Services;

public static class SettingsService
{
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static UserSettings Load()
    {
        return LoadWithResult().Settings;
    }

    public static SettingsLoadResult LoadWithResult()
    {
        if (!File.Exists(FilePath))
            return new SettingsLoadResult(new UserSettings(), null);

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
            var normalization = Normalize(settings);
            return new SettingsLoadResult(settings, null, normalization.RemovedLegacySearchCount,
                normalization.LegacySearchSettingsChanged);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SettingsLoadResult(new UserSettings(), exception);
        }
    }

    public static void Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static SettingsSaveResult TrySave(UserSettings settings)
    {
        try
        {
            Save(settings);
            return new SettingsSaveResult(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new SettingsSaveResult(exception);
        }
    }

    private static SettingsNormalizationResult Normalize(UserSettings settings)
    {
        settings.Theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        settings.EditorFontFamily = string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Cascadia Mono" : settings.EditorFontFamily;
        settings.EditorFontSize = double.IsFinite(settings.EditorFontSize) ? Math.Clamp(settings.EditorFontSize, 7, 72) : 15;
        settings.WindowWidth = double.IsFinite(settings.WindowWidth) ? Math.Max(1040, settings.WindowWidth) : 1380;
        settings.WindowHeight = double.IsFinite(settings.WindowHeight) ? Math.Max(680, settings.WindowHeight) : 860;
        settings.ToolPanelWidth = double.IsFinite(settings.ToolPanelWidth) ? Math.Max(280, settings.ToolPanelWidth) : 360;
        settings.ResultPanelHeight = double.IsFinite(settings.ResultPanelHeight) ? Math.Max(120, settings.ResultPanelHeight) : 220;
        if (settings.WindowLeft is not null && !double.IsFinite(settings.WindowLeft.Value)) settings.WindowLeft = null;
        if (settings.WindowTop is not null && !double.IsFinite(settings.WindowTop.Value)) settings.WindowTop = null;
        settings.WindowState = settings.WindowState == "Maximized" ? "Maximized" : "Normal";
        settings.RecentFiles ??= [];
        settings.FavoriteToolIds ??= [TextToolIds.RemoveLinesContaining, TextToolIds.Replace];
        settings.RecentSearches ??= [];
        settings.SearchState ??= new SearchInputState();
        settings.TransformState ??= new TransformInputState();

        settings.RecentFiles.RemoveAll(item => item is null);
        settings.FavoriteToolIds.RemoveAll(item => string.IsNullOrWhiteSpace(item));
        RemoveDuplicates(settings.FavoriteToolIds);
        settings.RecentSearches.RemoveAll(item => item is null);

        NormalizeSearch(settings.SearchState);
        var legacySearchSettingsChanged = settings.SearchState.Mode == SavedSearchMode.Advanced;
        var removedLegacySearchCount = 0;
        if (legacySearchSettingsChanged && !LegacySearchMigration.TryConvertAdvancedToSimple(settings.SearchState, out _))
            removedLegacySearchCount++;
        settings.SearchState = MigrateActiveSearch(settings.SearchState);
        NormalizeTransform(settings.TransformState);
        for (var index = settings.RecentSearches.Count - 1; index >= 0; index--)
        {
            var savedSearch = settings.RecentSearches[index];
            savedSearch.Search ??= new SearchInputState();
            savedSearch.Summary ??= string.Empty;
            NormalizeSearch(savedSearch.Search);
            if (savedSearch.Search.Mode == SavedSearchMode.Advanced)
            {
                legacySearchSettingsChanged = true;
                if (!LegacySearchMigration.TryConvertAdvancedToSimple(savedSearch.Search, out var simple))
                {
                    settings.RecentSearches.RemoveAt(index);
                    removedLegacySearchCount++;
                    continue;
                }
                savedSearch.Search = simple;
            }
            else
            {
                savedSearch.Search.Condition = LegacySearchMigration.BuildSimpleCondition(savedSearch.Search);
            }
        }

        for (var index = settings.RecentSearches.Count - 1; index >= 0; index--)
        {
            if (settings.RecentSearches.Take(index).Any(existing => SavedSearchHistory.HasSameCriteria(existing.Search, settings.RecentSearches[index].Search)))
                settings.RecentSearches.RemoveAt(index);
        }

        if (settings.RecentSearches.Count > SavedSearchHistory.MaximumCount)
            settings.RecentSearches.RemoveRange(SavedSearchHistory.MaximumCount, settings.RecentSearches.Count - SavedSearchHistory.MaximumCount);
        return new SettingsNormalizationResult(removedLegacySearchCount, legacySearchSettingsChanged);
    }

    private static SearchInputState MigrateActiveSearch(SearchInputState search)
    {
        if (search.Mode == SavedSearchMode.Advanced)
            return LegacySearchMigration.TryConvertAdvancedToSimple(search, out var simple) ? simple : new SearchInputState();

        search.Mode = SavedSearchMode.Simple;
        search.Condition = LegacySearchMigration.BuildSimpleCondition(search);
        return search;
    }

    private static void NormalizeSearch(SearchInputState search)
    {
        if (!Enum.IsDefined(search.Mode)) search.Mode = SavedSearchMode.Simple;
        search.SimpleAllTerms ??= [];
        search.SimpleAnyTerms ??= [];
        search.SimpleExcludeTerms ??= [];
        search.Condition ??= new SavedConditionNode();
        search.Options ??= new SavedSearchOptions();
        search.SimpleAllTerms.RemoveAll(item => item is null);
        search.SimpleAnyTerms.RemoveAll(item => item is null);
        search.SimpleExcludeTerms.RemoveAll(item => item is null);
        search.Options.ContextLines = Math.Max(0, search.Options.ContextLines);
        NormalizeCondition(search.Condition);
    }

    private static void NormalizeCondition(SavedConditionNode condition)
    {
        if (!Enum.IsDefined(condition.NodeType)) condition.NodeType = SavedConditionNodeType.Group;
        if (!Enum.IsDefined(condition.Operator)) condition.Operator = SavedConditionOperator.All;
        if (!Enum.IsDefined(condition.Kind)) condition.Kind = SavedTextConditionKind.Contains;
        condition.Value ??= string.Empty;
        condition.Children ??= [];
        condition.Children.RemoveAll(child => child is null);
        foreach (var child in condition.Children)
            NormalizeCondition(child);
    }

    private static void NormalizeTransform(TransformInputState transform)
    {
        transform.SelectedToolId = string.IsNullOrWhiteSpace(transform.SelectedToolId) ? TextToolIds.RemoveBefore : transform.SelectedToolId;
        transform.StartMarker ??= string.Empty;
        transform.EndMarker ??= string.Empty;
        transform.CharacterCount = Math.Max(0, transform.CharacterCount);
        transform.Value ??= string.Empty;
        transform.StartNumber = Math.Max(0, transform.StartNumber);
        transform.LineNumberSeparator ??= ": ";
        transform.ReplaceFrom ??= string.Empty;
        transform.ReplaceTo ??= string.Empty;
        transform.RemoveLinesContainingText ??= string.Empty;
    }

    private static void RemoveDuplicates(List<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        values.RemoveAll(value => !seen.Add(value));
    }
}

internal readonly record struct SettingsNormalizationResult(int RemovedLegacySearchCount, bool LegacySearchSettingsChanged);

public sealed record SettingsLoadResult(UserSettings Settings, Exception? Error, int RemovedLegacySearchCount = 0,
    bool NeedsSave = false)
{
    public bool Succeeded => Error is null;
}

public sealed record SettingsSaveResult(Exception? Error)
{
    public bool Succeeded => Error is null;
}

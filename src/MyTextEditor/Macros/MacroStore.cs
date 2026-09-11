using System.IO;
using System.Text.Json;
using MyTextEditor.Core.Macros;

namespace MyTextEditor.Macros;

public sealed class MacroStore
{
    private readonly string _path;
    public MacroStore(string? path = null) => _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyTextEditor", "macros.json");
    private sealed class Library
    {
        public int Version { get; set; } = 1;
        public List<JsonElement>? Macros { get; set; }
    }
    public List<MacroDefinition> Load()
    {
        if (!File.Exists(_path)) return [];
        var library = JsonSerializer.Deserialize<Library>(File.ReadAllText(_path)) ?? throw new InvalidDataException("매크로 목록이 비어 있습니다.");
        if (library.Version != 1 || library.Macros == null) throw new InvalidDataException("지원하지 않거나 손상된 매크로 목록입니다.");
        return library.Macros.Select(item => MacroJsonCodec.Deserialize(item.GetRawText())).ToList();
    }
    public void Save(IEnumerable<MacroDefinition> macros)
    {
        var items = macros.Select(m => JsonSerializer.Deserialize<JsonElement>(MacroJsonCodec.Serialize(m))).ToList();
        foreach (var item in items) _ = MacroJsonCodec.Deserialize(item.GetRawText());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var text = JsonSerializer.Serialize(new Library { Macros = items }, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, text, new System.Text.UTF8Encoding(false));
        File.Move(temporaryPath, _path, true);
    }
}

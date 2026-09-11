using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyTextEditor.Core.Macros;

public static class MacroJsonCodec
{
    private sealed class Envelope
    {
        public int Version { get; set; }
        public MacroDefinition? Macro { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Serialize(MacroDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(new Envelope { Version = 1, Macro = definition }, Options);
    }

    public static MacroDefinition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Options)
            ?? throw new JsonException("매크로 파일이 비어 있습니다.");
        if (envelope.Version != 1) throw new JsonException($"지원하지 않는 매크로 파일 버전입니다: {envelope.Version}");
        var macro = envelope.Macro ?? throw new JsonException("매크로 정의가 없습니다.");
        var errors = new TextMacroRunner().Validate(macro);
        if (errors.Count > 0) throw new JsonException(string.Join(Environment.NewLine, errors));
        return macro;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;

namespace Pulse.Core.Ipc;

/// <summary>
/// Source-generated JSON metadata for everything Pulse serialises: pipe messages, settings.json and state.json.
/// Enums as strings, nulls omitted, NaN/Infinity tolerated, comments and trailing commas accepted when reading files.
/// </summary>
[JsonSourceGenerationOptions(
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(PulseState))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AppTheme))]
[JsonSerializable(typeof(TrayIconMode))]
public sealed partial class IpcJsonContext : JsonSerializerContext
{
    /// <summary>Same metadata, indented: for files people may open in an editor.</summary>
    public static JsonSerializerOptions FileOptions => s_fileOptions ??= new JsonSerializerOptions(Default.Options!) { WriteIndented = true };

    private static JsonSerializerOptions? s_fileOptions;
}

/// <summary>RGB colours travel as "#RRGGBB" (a third of the size of {"R":..,"G":..,"B":..} for per-LED lists).</summary>
public sealed class RgbColorJsonConverter : JsonConverter<RgbColor>
{
    public override RgbColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        try
        {
            return text is null ? default : RgbColor.FromHex(text);
        }
        catch (FormatException ex)
        {
            throw new JsonException($"Invalid colour '{text}'.", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, RgbColor value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToHex());
}

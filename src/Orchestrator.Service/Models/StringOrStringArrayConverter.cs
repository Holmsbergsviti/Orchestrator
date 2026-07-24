// =====================================================================================
// FILE PURPOSE (in plain terms):
//   A small JSON reader/writer so a manifest field can be written EITHER as a single
//   string OR as a list of strings, and we always get a List<string> in code. Used by
//   ProgramEntry.Target so you can write "target": "all" or "target": ["pc-a", "pc-b"].
// =====================================================================================

using System.Text.Json;                    // low-level JSON reader/writer
using System.Text.Json.Serialization;      // the JsonConverter base type

namespace Orchestrator.Service.Models;     // groups this with the other data models

/// <summary>Deserializes a JSON string or array-of-strings into a <see cref="List{String}"/>.</summary>
public sealed class StringOrStringArrayConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)   // what kind of JSON value are we looking at?
        {
            case JsonTokenType.Null:
                return null;                                        // no value -> null (means "all machines")

            case JsonTokenType.String:
                var single = reader.GetString();                    // one string, e.g. "all" or a hostname
                return string.IsNullOrWhiteSpace(single)            // blank string -> treat as no value
                    ? null
                    : new List<string> { single };                  // otherwise a one-item list

            case JsonTokenType.StartArray:
                var list = new List<string>();                      // read every string in the array
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;         // end of the array
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var v = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) list.Add(v);            // keep non-blank entries
                    }
                }
                return list;

            default:
                throw new JsonException($"Unexpected token '{reader.TokenType}' for 'target' (expected string or array).");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null)                                          // null -> write JSON null
        {
            writer.WriteNullValue();
            return;
        }
        if (value.Count == 1)                                       // a single target -> write a plain string (tidier)
        {
            writer.WriteStringValue(value[0]);
            return;
        }
        writer.WriteStartArray();                                   // otherwise write it as an array
        foreach (var v in value) writer.WriteStringValue(v);
        writer.WriteEndArray();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiroBot.Plugin.OneBotServer.Protocol;

public sealed record OneBotSegment(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, object?> Data)
{
    public static OneBotSegment Text(string text) => new("text", new Dictionary<string, object?> { ["text"] = text });
}

public sealed record OneBotActionRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("params")] IReadOnlyDictionary<string, JsonElement> Params,
    [property: JsonPropertyName("echo")] JsonElement? Echo);

public sealed record OneBotResponse<T>(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("retcode")] int RetCode,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("wording")] string? Wording = null,
    [property: JsonPropertyName("echo")] JsonElement? Echo = null)
{
    public static OneBotResponse<T> Ok(T? data, JsonElement? echo = null) => new("ok", 0, data, Echo: echo);
    public static OneBotResponse<object?> Failed(int retCode, string message, JsonElement? echo = null) =>
        new("failed", retCode, null, message, message, echo);
}

public static class OneBotParameters
{
    public static IReadOnlyDictionary<string, JsonElement> ObjectOrEmpty(JsonElement? value)
    {
        if (value is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }) return new Dictionary<string, JsonElement>();
        if (value.Value.ValueKind != JsonValueKind.Object) throw new OneBotParameterException("params must be an object");
        return value.Value.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    public static long RequiredInt(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        OptionalInt(values, name) ?? throw new OneBotParameterException($"{name} must be an integer");

    public static long? OptionalInt(IReadOnlyDictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        throw new OneBotParameterException($"{name} must be an integer");
    }

    public static bool Bool(IReadOnlyDictionary<string, JsonElement> values, string name, bool fallback = false)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var number) && number is 0 or 1 => number == 1,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when value.GetString() is "0" => false,
            JsonValueKind.String when value.GetString() is "1" => true,
            _ => throw new OneBotParameterException($"{name} must be a boolean"),
        };
    }

    public static string String(IReadOnlyDictionary<string, JsonElement> values, string name, string? fallback = null)
    {
        if (!values.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return fallback ?? throw new OneBotParameterException($"{name} must be a string");
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => throw new OneBotParameterException($"{name} must be a string"),
        };
    }
}

public sealed class OneBotParameterException(string message) : ArgumentException(message);

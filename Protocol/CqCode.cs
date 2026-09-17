namespace ShiroBot.Plugin.OneBotServer.Protocol;

public static class CqCode
{
    public static IReadOnlyList<OneBotSegment> Parse(string message)
    {
        var result = new List<OneBotSegment>();
        var cursor = 0;
        while (cursor < message.Length)
        {
            var start = message.IndexOf("[CQ:", cursor, StringComparison.Ordinal);
            if (start < 0) { AppendText(result, Decode(message[cursor..], false)); break; }
            AppendText(result, Decode(message[cursor..start], false));
            var end = message.IndexOf(']', start + 4);
            if (end < 0) { AppendText(result, Decode(message[start..], false)); break; }
            var parts = message[(start + 4)..end].Split(',');
            if (parts.Length == 0 || !IsName(parts[0]) || parts.Skip(1).Any(part => part.IndexOf('=') <= 0 || !IsName(part[..part.IndexOf('=')])))
            {
                AppendText(result, Decode(message[start..(end + 1)], false));
            }
            else
            {
                var data = new Dictionary<string, object?>();
                foreach (var part in parts.Skip(1)) data[part[..part.IndexOf('=')]] = Decode(part[(part.IndexOf('=') + 1)..], true);
                result.Add(new OneBotSegment(parts[0], data));
            }
            cursor = end + 1;
        }
        return result;
    }

    public static string Serialize(IEnumerable<OneBotSegment> segments) => string.Concat(segments.Select(segment =>
    {
        if (segment.Type == "text") return Encode(Convert.ToString(segment.Data.GetValueOrDefault("text")) ?? string.Empty, false);
        var parameters = segment.Data.Where(pair => pair.Value is not null)
            .Select(pair => $"{pair.Key}={Encode(Convert.ToString(pair.Value) ?? string.Empty, true)}");
        return $"[CQ:{segment.Type}{(parameters.Any() ? "," + string.Join(',', parameters) : string.Empty)}]";
    }));

    private static void AppendText(List<OneBotSegment> segments, string text)
    {
        if (text.Length == 0) return;
        if (segments.LastOrDefault() is { Type: "text" } previous)
            segments[^1] = OneBotSegment.Text((Convert.ToString(previous.Data.GetValueOrDefault("text")) ?? string.Empty) + text);
        else segments.Add(OneBotSegment.Text(text));
    }

    private static bool IsName(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static string Decode(string value, bool parameter) => value.Replace("&amp;", "&", StringComparison.Ordinal).Replace("&#91;", "[", StringComparison.Ordinal).Replace("&#93;", "]", StringComparison.Ordinal).Replace(parameter ? "&#44;" : "\0", parameter ? "," : "\0", StringComparison.Ordinal);
    private static string Encode(string value, bool parameter) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("[", "&#91;", StringComparison.Ordinal).Replace("]", "&#93;", StringComparison.Ordinal).Replace(parameter ? "," : "\0", parameter ? "&#44;" : "\0", StringComparison.Ordinal);
}

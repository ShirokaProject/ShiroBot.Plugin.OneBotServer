using System.Globalization;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.Plugin.OneBotServer.Protocol;

public static class MessageSegments
{
    public static IReadOnlyList<OneBotSegment> FromQq(IEnumerable<QIncomingSegment> segments) => segments.Select(segment => segment switch
    {
        QIncomingText value => OneBotSegment.Text(value.Text),
        QIncomingMention value => Segment("at", ("qq", value.UserId), ("name", value.Name)),
        QIncomingMentionAll => Segment("at", ("qq", "all")),
        QIncomingFace value => Segment("face", ("id", value.FaceId), ("is_large", value.IsLarge)),
        QIncomingReply value => Segment("reply", ("id", value.MessageSeq)),
        QIncomingImage value => Segment("image", ("file", value.ResourceId), ("url", value.TempUrl), ("width", value.Width), ("height", value.Height), ("summary", value.Summary), ("sub_type", value.SubType)),
        QIncomingRecord value => Segment("record", ("file", value.ResourceId), ("url", value.TempUrl), ("duration", (long)value.Duration.TotalSeconds)),
        QIncomingVideo value => Segment("video", ("file", value.ResourceId), ("url", value.TempUrl), ("width", value.Width), ("height", value.Height), ("duration", (long)value.Duration.TotalSeconds)),
        QIncomingFile value => Segment("file", ("file", value.FileName), ("file_id", value.FileId), ("file_size", value.FileSize.ToString(CultureInfo.InvariantCulture)), ("name", value.FileName)),
        QIncomingForward value => Segment("forward", ("id", value.ForwardId), ("title", value.Title), ("preview", value.Preview), ("summary", value.Summary)),
        QIncomingLightApp value => Segment("json", ("data", value.JsonPayload), ("app_name", value.AppName)),
        QIncomingXml value => Segment("xml", ("data", value.XmlPayload), ("service_id", value.ServiceId)),
        QIncomingMarkdown value => Segment("markdown", ("content", value.Content)),
        QIncomingMarketFace value => Segment("market_face", ("emoji_id", value.EmojiId), ("url", value.Url), ("emoji_package_id", value.EmojiPackageId), ("key", value.Key), ("summary", value.Summary)),
        _ => throw new NotSupportedException($"Unsupported QQ incoming segment: {segment.GetType().Name}"),
    }).ToArray();

    public static IReadOnlyList<QOutgoingSegment> ToQq(IEnumerable<OneBotSegment> segments) => segments.Select(ToQq).ToArray();

    public static IReadOnlyList<MessageSegment> ToGeneric(IEnumerable<OneBotSegment> segments) => segments.Select<OneBotSegment, MessageSegment>(segment => segment.Type switch
    {
        "text" => new TextSegment(GetString(segment, "text")),
        "at" when GetString(segment, "qq") == "all" => new MentionAllSegment(),
        "at" => new MentionSegment(GetString(segment, "qq")) { DisplayName = GetOptionalString(segment, "name") },
        "reply" => new QuoteSegment(GetString(segment, "id")),
        "face" => new EmojiSegment(GetString(segment, "id")),
        "image" => new ImageSegment(GetFile(segment)) { Summary = GetOptionalString(segment, "summary") },
        "record" => new AudioSegment(GetFile(segment)),
        "video" => new VideoSegment(GetFile(segment)) { ThumbnailUri = GetOptionalString(segment, "cover") },
        "file" => new FileSegment(GetFirstString(segment, ["file", "url", "file_id", "id"]))
        {
            ResourceId = GetFirstString(segment, ["file_id", "id"], required: false),
            FileName = GetOptionalString(segment, "name") ?? GetOptionalString(segment, "file"),
            FileSize = GetOptionalLong(segment, "file_size") ?? GetOptionalLong(segment, "size")
        },
        _ => new RawSegment("onebot", segment.Type, segment.Data),
    }).ToArray();

    private static QOutgoingSegment ToQq(OneBotSegment segment) => segment.Type switch
    {
        "text" => new QOutgoingText(GetString(segment, "text")),
        "at" when GetString(segment, "qq") == "all" => new QOutgoingMentionAll(),
        "at" => new QOutgoingMention(GetLong(segment, "qq")),
        "face" => new QOutgoingFace(GetString(segment, "id"), GetBool(segment, "is_large")),
        "reply" => new QOutgoingReply(GetLong(segment, "id")),
        "image" when GetOptionalString(segment, "type") != "flash" => new QOutgoingImage(GetFile(segment)) { Summary = GetOptionalString(segment, "summary"), SubType = GetOptionalString(segment, "sub_type") },
        "record" => new QOutgoingRecord(GetFile(segment)),
        "video" => new QOutgoingVideo(GetFile(segment)) { ThumbUri = GetOptionalString(segment, "cover") },
        "json" or "light_app" => new QOutgoingLightApp(GetString(segment, "data")),
        _ => throw new NotSupportedException($"Unsupported OneBot message segment: {segment.Type}"),
    };

    private static OneBotSegment Segment(string type, params (string Key, object? Value)[] values) => new(type, values.Where(value => value.Value is not null).ToDictionary(value => value.Key, value => value.Value));
    private static string GetFile(OneBotSegment segment) => GetOptionalString(segment, "file") ?? GetString(segment, "url");
    private static string GetFirstString(OneBotSegment segment, IReadOnlyList<string> names, bool required = true)
    {
        foreach (var name in names)
        {
            if (GetOptionalString(segment, name) is { Length: > 0 } value) return value;
        }
        return required ? throw new ArgumentException($"OneBot {segment.Type} requires one of: {string.Join(", ", names)}") : string.Empty;
    }
    private static string GetString(OneBotSegment segment, string name) => GetOptionalString(segment, name) ?? throw new ArgumentException($"OneBot {segment.Type}.{name} must be a non-empty string");
    private static string? GetOptionalString(OneBotSegment segment, string name) => segment.Data.TryGetValue(name, out var value) && value is not null ? Convert.ToString(value) : null;
    private static long GetLong(OneBotSegment segment, string name) => long.TryParse(GetString(segment, name), out var result) ? result : throw new ArgumentException($"OneBot {segment.Type}.{name} must be an integer");
    private static long? GetOptionalLong(OneBotSegment segment, string name) => GetOptionalString(segment, name) is { } value && long.TryParse(value, out var result) ? result : null;
    private static bool GetBool(OneBotSegment segment, string name) => GetOptionalString(segment, name) is "true" or "1";
}

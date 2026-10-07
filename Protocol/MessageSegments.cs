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
        QIncomingReply value => Segment("reply", ("id", value.MessageId)),
        QIncomingImage value => Segment("image", ("file", value.ResourceId), ("url", value.TempUrl), ("width", value.Width), ("height", value.Height), ("summary", value.Summary), ("sub_type", value.SubType)),
        QIncomingRecord value => Segment("record", ("file", value.ResourceId), ("url", value.TempUrl), ("duration", (long)value.Duration.TotalSeconds)),
        QIncomingVideo value => Segment("video", ("file", value.ResourceId), ("url", value.TempUrl), ("width", value.Width), ("height", value.Height), ("duration", (long)value.Duration.TotalSeconds)),
        QIncomingFile value => Segment("file", ("file", value.FileName), ("file_id", value.FileId), ("fid", value.FileId), ("file_size", value.FileSize.ToString(CultureInfo.InvariantCulture)), ("name", value.FileName)),
        QIncomingForward value => Segment("forward", ("id", value.ForwardId), ("title", value.Title), ("preview", value.Preview), ("summary", value.Summary)),
        QIncomingLightApp value => Segment("json", ("data", value.JsonPayload), ("app_name", value.AppName)),
        QIncomingXml value => Segment("xml", ("data", value.XmlPayload), ("service_id", value.ServiceId)),
        QIncomingMarkdown value => Segment("markdown", ("content", value.Content)),
        QIncomingMarketFace value => Segment("mface", ("emoji_id", value.EmojiId), ("emoji_package_id", value.EmojiPackageId), ("key", value.Key), ("summary", value.Summary), ("url", value.Url)),
        _ => throw new NotSupportedException($"Unsupported QQ incoming segment: {segment.GetType().Name}"),
    }).ToArray();

    /// <summary>Convert common SDK content; cards are explicitly flattened, Markdown uses the documented extension.</summary>
    public static IReadOnlyList<OneBotSegment> FromGeneric(IEnumerable<MessageSegment> segments, string? platform = null) =>
        segments.Select(segment => FromGeneric(segment, platform)).ToArray();

    private static OneBotSegment FromGeneric(MessageSegment segment, string? platform) => segment switch
    {
        TextSegment text => OneBotSegment.Text(text.Text),
        MarkdownSegment markdown => Segment("markdown", ("content", markdown.Content), ("plain_text", markdown.PlainTextFallback)),
        CardSegment card => OneBotSegment.Text(((TextSegment)MessagePreparation.Prepare(
            new OutgoingMessage { Segments = [card], AllowedFallbacks = MessageFallbackOptions.CardAsText },
            new MessageCapabilities { NativeFeatures = MessageFeatures.Text }).Message.Segments.Single()).Text),
        MentionSegment mention => Segment("at", ("qq", mention.UserId), ("name", mention.DisplayName)),
        MentionAllSegment => Segment("at", ("qq", "all")),
        QuoteSegment quote => Segment("reply", ("id", quote.MessageId)),
        EmojiSegment emoji when platform is "qq" or "qq-official" or "milky" => Segment("face", ("id", emoji.Id), ("name", emoji.Name)),
        EmojiSegment { Name: { Length: > 0 } name } => OneBotSegment.Text(name),
        EmojiSegment => throw new NotSupportedException("Platform emoji has no known OneBot face mapping or text fallback."),
        ImageSegment image => Segment("image", ("file", image.Uri), ("resource_id", image.ResourceId), ("summary", image.Summary), ("width", image.Width), ("height", image.Height)),
        AudioSegment audio => Segment("record", ("file", audio.Uri), ("resource_id", audio.ResourceId), ("duration", audio.Duration?.TotalSeconds), ("transcript", audio.Transcript)),
        VideoSegment video => Segment("video", ("file", video.Uri), ("resource_id", video.ResourceId), ("cover", video.ThumbnailUri)),
        FileSegment file => Segment("file", ("file", file.FileName ?? file.Uri), ("url", string.IsNullOrWhiteSpace(file.Uri) ? null : file.Uri), ("file_id", string.IsNullOrWhiteSpace(file.ResourceId) ? file.Uri : file.ResourceId), ("fid", string.IsNullOrWhiteSpace(file.ResourceId) ? file.Uri : file.ResourceId), ("file_size", file.FileSize?.ToString(CultureInfo.InvariantCulture)), ("name", file.FileName)),
        RawSegment { Payload: QIncomingSegment incoming } => FromQq([incoming]).Single(),
        RawSegment { Platform: "onebot", Payload: IReadOnlyDictionary<string, object?> data } raw => new(raw.Kind, new Dictionary<string, object?>(data)),
        RawSegment => throw new NotSupportedException("Raw segment has no known OneBot mapping."),
        _ => throw new NotSupportedException("Unsupported SDK message segment: " + segment.GetType().Name)
    };

    public static IReadOnlyList<QOutgoingSegment> ToQq(IEnumerable<OneBotSegment> segments) => segments.Select(ToQq).ToArray();

    public static IReadOnlyList<MessageSegment> ToGeneric(IEnumerable<OneBotSegment> segments) => segments.Select<OneBotSegment, MessageSegment>(segment => segment.Type switch
    {
        "text" => new TextSegment(GetString(segment, "text")),
        "markdown" => new MarkdownSegment(GetString(segment, "content")) { PlainTextFallback = GetOptionalString(segment, "plain_text") },
        "share" => new CardSegment { Title = GetString(segment, "title"), Url = GetString(segment, "url"), Description = GetOptionalString(segment, "content"), ImageUrl = GetOptionalString(segment, "image") },
        "json" or "light_app" => new RawSegment("qq", "light_app", new QOutgoingLightApp(GetString(segment, "data"))),
        "at" when GetString(segment, "qq") == "all" => new MentionAllSegment(),
        "at" => new MentionSegment(GetString(segment, "qq")) { DisplayName = GetOptionalString(segment, "name") },
        "reply" => new QuoteSegment(GetString(segment, "id")),
        "face" => new EmojiSegment(GetString(segment, "id")),
        "image" => new ImageSegment(GetFile(segment)) { ResourceId = GetOptionalString(segment, "resource_id"), Summary = GetOptionalString(segment, "summary"), Width = OptionalInt(segment, "width"), Height = OptionalInt(segment, "height") },
        "record" => new AudioSegment(GetFile(segment)) { ResourceId = GetOptionalString(segment, "resource_id"), Duration = GetOptionalLong(segment, "duration") is { } seconds ? TimeSpan.FromSeconds(seconds) : null, Transcript = GetOptionalString(segment, "transcript") },
        "video" => new VideoSegment(GetFile(segment)) { ResourceId = GetOptionalString(segment, "resource_id"), ThumbnailUri = GetOptionalString(segment, "cover") },
        "file" => new FileSegment(GetFirstString(segment, ["url", "file", "file_id", "id"]))
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
        "at" => new QOutgoingMention(GetLong(segment, "qq").ToString(CultureInfo.InvariantCulture)),
        "face" => new QOutgoingFace(GetString(segment, "id"), GetBool(segment, "is_large")),
        "reply" => new QOutgoingReply(GetLong(segment, "id").ToString(CultureInfo.InvariantCulture)),
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
    private static int? OptionalInt(OneBotSegment segment, string name) => GetOptionalLong(segment, name) is { } value ? checked((int)value) : null;
    private static bool GetBool(OneBotSegment segment, string name) => GetOptionalString(segment, name) is "true" or "1";
}

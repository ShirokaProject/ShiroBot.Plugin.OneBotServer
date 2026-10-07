using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShiroBot.Plugin.OneBotServer.Infrastructure;

public sealed record RequestFlag(string Kind, long? GroupId = null, long? Sequence = null, string? InitiatorUid = null, bool Filtered = false, string? RequestType = null, string? NativeToken = null, string? EncodedRequest = null);

public static class RequestFlagCodec
{
    private static readonly byte[] DefaultSigningKey = SHA256.HashData(Encoding.UTF8.GetBytes("ShiroBot.Plugin.OneBotServer/request-flags/v1"));

    public static string Encode(RequestFlag flag) => Encode(flag, DefaultSigningKey);

    public static RequestFlag Decode(string value) => Decode(value, DefaultSigningKey);

    public static string Encode(RequestFlag flag, ReadOnlySpan<byte> signingKey)
    {
        ValidateKey(signingKey);
        Validate(flag);
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(flag));
        return $"shiro.1.{payload}.{Signature(payload, signingKey)}";
    }

    public static RequestFlag Decode(string value, ReadOnlySpan<byte> signingKey)
    {
        ValidateKey(signingKey);
        var parts = value.Split('.');
        if (parts.Length != 4 || parts[0] != "shiro" || parts[1] != "1") throw new ArgumentException("Invalid or unsupported OneBot request flag");
        var suppliedSignature = Encoding.ASCII.GetBytes(parts[3]);
        var expectedSignature = Encoding.ASCII.GetBytes(Signature(parts[2], signingKey));
        if (suppliedSignature.Length != expectedSignature.Length || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            throw new ArgumentException("Invalid or unsupported OneBot request flag");
        try
        {
            var result = JsonSerializer.Deserialize<RequestFlag>(Base64UrlDecode(parts[2])) ?? throw new ArgumentException("Invalid OneBot request flag payload");
            Validate(result);
            return result;
        }
        catch (JsonException exception) { throw new ArgumentException("Invalid OneBot request flag payload", exception); }
    }

    private static void Validate(RequestFlag flag)
    {
        if (flag.Kind == "friend" && !string.IsNullOrWhiteSpace(flag.InitiatorUid)) return;
        if (flag.Kind == "group" && flag.GroupId >= 0 && (flag.Sequence >= 0 || !string.IsNullOrWhiteSpace(flag.EncodedRequest)) && flag.RequestType is "join_request" or "invited_join_request") return;
        if (flag.Kind == "invitation" && flag.GroupId >= 0 && (flag.Sequence >= 0 || !string.IsNullOrWhiteSpace(flag.NativeToken))) return;
        throw new ArgumentException("Invalid OneBot request flag payload");
    }

    private static void ValidateKey(ReadOnlySpan<byte> signingKey)
    {
        if (signingKey.Length < 16) throw new ArgumentException("The request flag signing key must contain at least 16 bytes", nameof(signingKey));
    }

    private static string Signature(string payload, ReadOnlySpan<byte> signingKey) =>
        Base64Url(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes("shiro-onebot-request-v1\0" + payload)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Base64UrlDecode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
}

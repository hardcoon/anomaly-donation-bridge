using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.YouTubeChat;

public static partial class YouTubeVideoLink
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdPattern();

    public static string Parse(string? value)
    {
        value = (value ?? string.Empty).Trim();
        if (VideoIdPattern().IsMatch(value)) return value;
        if (value.Length > 2048) throw InvalidLink();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw InvalidLink();

        var path = uri.AbsolutePath.Trim('/').Split('/');
        string? id = null;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) && path.Length == 1)
            id = path[0];
        else if (uri.Host.ToLowerInvariant() is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            if (path.Length == 2 && path[0] is "live" or "embed" or "shorts") id = path[1];
            else if (path.Length == 1 && path[0] == "watch")
            {
                var ids = uri.Query.TrimStart('?').Split('&')
                    .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == "v")
                    .Select(pair => Uri.UnescapeDataString(pair[1])).ToArray();
                if (ids.Length == 1) id = ids[0];
            }
        }
        return id is not null && VideoIdPattern().IsMatch(id) ? id : throw InvalidLink();
    }

    public static string Canonical(string videoId) => "https://www.youtube.com/watch?v=" + Parse(videoId);

    public static string NormalizeKey(string? value)
    {
        var key = (value ?? string.Empty).Trim();
        if (key.Length is < 20 or > 256 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new YouTubeChatException("api_key", "Вставьте ключ API из Google Cloud, а не ссылку или OAuth-токен.");
        return key;
    }

    private static YouTubeChatException InvalidLink() => new("video_link",
        "Вставьте ссылку на конкретную трансляцию YouTube (watch?v=… или /live/…), а не на канал.");
}

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AnomalyDonationBridge.YouTubeChat;

// Read-only guest web chat. The website supplies its current client context.
// No account, borrowed application key, browser profile or persisted cookies.
public sealed class AnonymousYouTubeChatTransport : IYouTubeChatTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private JsonElement _context;
    private string _clientVersion = "", _publicWebKey = "", _initialCursor = "", _videoId = "";
    private IReadOnlyList<YouTubeChatMessage> _initialMessages = [];
    private bool _liveViewChosen;
    public bool RequiresApiKey => false;

    public AnonymousYouTubeChatTransport(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            CookieContainer = new CookieContainer(), UseCookies = true
        }) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task<YouTubeBroadcast> ResolveAsync(string apiKey, string videoId, CancellationToken token)
    {
        videoId = YouTubeVideoLink.Parse(videoId);
        _initialCursor = ""; _initialMessages = []; _liveViewChosen = false;
        using var request = Request(HttpMethod.Get, YouTubeVideoLink.Canonical(videoId) + "&hl=ru");
        var html = await ReadTextAsync(request, token).ConfigureAwait(false);
        using var config = WebChatJson.Assignment(html, "ytcfg.set(", "INNERTUBE_CONTEXT");
        using var data = WebChatJson.Assignment(html, "ytInitialData", "contents");
        using var player = WebChatJson.Assignment(html, "ytInitialPlayerResponse", "playabilityStatus", required: false);
        var root = config!.RootElement;
        _context = root.GetProperty("INNERTUBE_CONTEXT").Clone();
        _clientVersion = WebChatJson.Text(root, "INNERTUBE_CLIENT_VERSION");
        if (_clientVersion.Length == 0 && _context.TryGetProperty("client", out var client))
            _clientVersion = WebChatJson.Text(client, "clientVersion");
        _publicWebKey = WebChatJson.Text(root, "INNERTUBE_API_KEY");
        if (_clientVersion.Length == 0) throw FormatError();
        var renderer = WebChatJson.Find(data!.RootElement, "liveChatRenderer");
        if (renderer is null || WebChatJson.Flag(renderer.Value, "isReplay"))
            throw new YouTubeChatException("no_chat", "Публичный чат текущего эфира не найден. Проверьте ссылку на прямую трансляцию.");
        var continuation = WebChatJson.Continuation(renderer.Value);
        if (continuation.Cursor.Length == 0) throw FormatError();
        _initialCursor = continuation.Cursor;
        _videoId = videoId;
        _initialMessages = ParseMessages(renderer.Value, historical: true);
        var title = "Трансляция YouTube";
        var author = "";
        if (player is not null && player.RootElement.TryGetProperty("videoDetails", out var details))
        { title = ChatText.Clean(WebChatJson.Text(details, "title"), 300); author = ChatText.Clean(WebChatJson.Text(details, "author"), 150); }
        return new(videoId, "anonymous:" + videoId, title, author);
    }

    public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string apiKey, string chatId, string? pageToken,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (_initialCursor.Length == 0 || chatId != "anonymous:" + _videoId)
            throw new YouTubeChatException("anonymous_session", "Подключите анонимный чат YouTube заново.");
        var cursor = string.IsNullOrEmpty(pageToken) ? _initialCursor : pageToken;
        if (string.IsNullOrEmpty(pageToken) && _initialMessages.Count > 0)
            yield return new(_initialMessages, cursor);
        while (!token.IsCancellationRequested)
        {
            var url = "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?prettyPrint=false";
            if (_publicWebKey.Length > 0) url += "&key=" + Uri.EscapeDataString(_publicWebKey);
            using var request = Request(HttpMethod.Post, url);
            request.Headers.Add("Origin", "https://www.youtube.com");
            request.Headers.Referrer = new Uri(YouTubeVideoLink.Canonical(_videoId));
            request.Headers.Add("X-YouTube-Client-Name", "1");
            request.Headers.Add("X-YouTube-Client-Version", _clientVersion);
            request.Content = new StringContent(JsonSerializer.Serialize(new { context = _context, continuation = cursor }), Encoding.UTF8, "application/json");
            var text = await ReadTextAsync(request, token).ConfigureAwait(false);
            using var response = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
            var continuation = WebChatJson.Find(response.RootElement, "liveChatContinuation");
            if (continuation is null)
                throw new YouTubeChatException("ended_or_disabled", "Эфир завершён или его публичный чат больше недоступен.");
            var next = WebChatJson.Continuation(continuation.Value);
            // The watch page's menu contains short UI identifiers, not usable
            // polling tokens. The first API response supplies the full cursor
            // for the second standard view, unfiltered "Live chat".
            if (!_liveViewChosen && WebChatJson.Find(continuation.Value, "sortFilterSubMenuRenderer") is { } selector &&
                selector.TryGetProperty("subMenuItems", out var views) && views.ValueKind == JsonValueKind.Array &&
                views.GetArrayLength() >= 2 && WebChatJson.Find(views[1], "reloadContinuationData") is { } choice &&
                WebChatJson.Text(choice, "continuation") is { Length: > 100 } allCursor)
            { next = (allCursor, 1000); _liveViewChosen = true; }
            if (next.Cursor.Length == 0)
            {
                yield return new(ParseMessages(continuation.Value, historical: false), "", Ended: true);
                yield break;
            }
            yield return new(ParseMessages(continuation.Value, historical: false), next.Cursor);
            // The consumer has acknowledged this batch before MoveNext resumes.
            cursor = next.Cursor;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(next.TimeoutMilliseconds, 1000, 30000)), token).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<YouTubeChatMessage> ParseMessages(JsonElement chat, bool historical)
    {
        var messages = new List<YouTubeChatMessage>();
        if (!chat.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return messages;
        foreach (var action in actions.EnumerateArray())
        {
            if (!action.TryGetProperty("addChatItemAction", out var add) || !add.TryGetProperty("item", out var item)) continue;
            JsonElement? renderer = null;
            foreach (var kind in new[] { "liveChatTextMessageRenderer", "liveChatPaidMessageRenderer", "liveChatPaidStickerRenderer", "liveChatMembershipItemRenderer" })
                if (item.TryGetProperty(kind, out var candidate)) { renderer = candidate; break; }
            if (renderer is not { } row) continue;
            var id = WebChatJson.Text(row, "id");
            var userId = WebChatJson.Text(row, "authorExternalChannelId");
            if (id.Length == 0 || userId.Length == 0) continue;
            var body = row.TryGetProperty("message", out var message) ? WebChatJson.Runs(message) :
                row.TryGetProperty("headerSubtext", out var subtext) ? WebChatJson.Runs(subtext) : "[Стикер]";
            if (body.Length == 0) continue;
            DateTimeOffset? at = null;
            if (long.TryParse(WebChatJson.Text(row, "timestampUsec"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usec))
                try { at = DateTimeOffset.FromUnixTimeMilliseconds(usec / 1000); } catch (ArgumentOutOfRangeException) { }
            var owner = false; var moderator = false;
            if (row.TryGetProperty("authorBadges", out var badges) && badges.ValueKind == JsonValueKind.Array)
                foreach (var badge in badges.EnumerateArray())
                {
                    var icon = WebChatJson.Find(badge, "iconType");
                    var kind = icon?.ValueKind == JsonValueKind.String ? icon.Value.GetString() : "";
                    owner |= kind == "OWNER"; moderator |= kind == "MODERATOR";
                }
            messages.Add(new(id, userId, row.TryGetProperty("authorName", out var name) ? WebChatJson.Runs(name) : "",
                ChatText.Clean(body), at, 1, owner, moderator, historical || at is null));
        }
        return messages;
    }

    private static HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("ru,en-US;q=0.8");
        return request;
    }
    private async Task<string> ReadTextAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode == 429)
                throw new YouTubeChatException("rate_limit", "YouTube ограничил частоту запросов анонимного чата.", true, TimeSpan.FromMinutes(1));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.Found or HttpStatusCode.SeeOther)
                throw new YouTubeChatException("guest_blocked", "YouTube не разрешил анонимное чтение этого эфира. Можно использовать режим с API-ключом.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new YouTubeChatException("not_found", "Эфир YouTube не найден или уже завершён.");
            if (response.StatusCode == HttpStatusCode.BadRequest)
                throw new YouTubeChatException("anonymous_cursor", "Позиция анонимного чата устарела. Отключите и подключите его заново.");
            if (!response.IsSuccessStatusCode)
                throw new YouTubeChatException("network", "YouTube временно не отвечает.", true);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                output.Write(buffer, 0, count);
                if (output.Length > 8_388_608) throw FormatError();
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new YouTubeChatException("timeout", "YouTube не ответил вовремя.", true); }
        catch (HttpRequestException) { throw new YouTubeChatException("network", "Нет связи с публичным чатом YouTube.", true); }
    }
    private static YouTubeChatException FormatError() => new("anonymous_format",
        "YouTube изменил формат публичного чата или ограничил доступ. Попробуйте режим с API-ключом.");
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

internal static class WebChatJson
{
    internal static JsonDocument? Assignment(string html, string marker, string requiredProperty, bool required = true)
    {
        var offset = 0;
        while ((offset = html.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            offset += marker.Length;
            var start = html.IndexOf('{', offset);
            if (start < 0 || start - offset > 50) continue;
            var quoted = false; var escaped = false; var depth = 0;
            for (var i = start; i < html.Length; i++)
            {
                var c = html[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try
                    {
                        var result = JsonDocument.Parse(html.AsMemory(start, i - start + 1), new JsonDocumentOptions { MaxDepth = 128 });
                        if (result.RootElement.TryGetProperty(requiredProperty, out _)) return result;
                        result.Dispose();
                    }
                    catch (JsonException) { }
                    break;
                }
            }
        }
        return required ? throw new YouTubeChatException("anonymous_format", "Не удалось прочитать публичный чат YouTube. Проверьте ссылку или попробуйте режим с API-ключом.") : null;
    }
    internal static JsonElement? Find(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(property, out var direct)) return direct;
            foreach (var item in root.EnumerateObject()) if (Find(item.Value, property) is { } found) return found;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray()) if (Find(item, property) is { } found) return found;
        return null;
    }
    internal static string Text(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    internal static bool Flag(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    internal static string Runs(JsonElement value)
    {
        var simple = Text(value, "simpleText");
        if (simple.Length > 0) return simple;
        if (!value.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return "";
        var builder = new StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            builder.Append(Text(run, "text"));
            if (run.TryGetProperty("emoji", out var emoji))
            {
                var id = Text(emoji, "emojiId");
                if (emoji.TryGetProperty("shortcuts", out var shortcuts) && shortcuts.ValueKind == JsonValueKind.Array && shortcuts.GetArrayLength() > 0)
                    builder.Append(shortcuts[0].GetString());
                else if (!id.StartsWith("UC", StringComparison.Ordinal) && id.Length < 20) builder.Append(id);
                else builder.Append(" [смайл] ");
            }
            if (builder.Length >= 4000) break;
        }
        return builder.ToString();
    }
    internal static (string Cursor, int TimeoutMilliseconds) Continuation(JsonElement root)
    {
        if (!root.TryGetProperty("continuations", out var continuations) || continuations.ValueKind != JsonValueKind.Array) return ("", 5000);
        foreach (var item in continuations.EnumerateArray())
            foreach (var kind in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData" })
                if (item.TryGetProperty(kind, out var data))
                {
                    var cursor = Text(data, "continuation");
                    if (cursor.Length > 0) return (cursor, data.TryGetProperty("timeoutMs", out var timeout) && timeout.TryGetInt32(out var ms) ? ms : 5000);
                }
        return ("", 5000);
    }
}

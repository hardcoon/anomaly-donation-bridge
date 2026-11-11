using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using AnomalyDonationBridge.YouTubeChat.Protocol;

namespace AnomalyDonationBridge.YouTubeChat;

public sealed class YouTubeChatTransport : IYouTubeChatTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly GrpcChannel? _channel;
    private readonly V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient _client;

    public YouTubeChatTransport(HttpClient? httpClient = null, CallInvoker? callInvoker = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
        if (callInvoker is null)
        {
            _channel = GrpcChannel.ForAddress("https://youtube.googleapis.com", new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15),
                    KeepAlivePingDelay = TimeSpan.FromMinutes(2),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
                },
                MaxReceiveMessageSize = 16 * 1024 * 1024
            });
            callInvoker = _channel.CreateCallInvoker();
        }
        _client = new(callInvoker);
    }

    public async Task<YouTubeBroadcast> ResolveAsync(string apiKey, string videoId, CancellationToken cancellationToken)
    {
        apiKey = YouTubeVideoLink.NormalizeKey(apiKey);
        videoId = YouTubeVideoLink.Parse(videoId);
        // Fixed host, no user URL fetching and no key in the query string.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/youtube/v3/videos?part=snippet,liveStreamingDetails&id=" + videoId +
            "&fields=items(id,snippet(title,channelTitle),liveStreamingDetails(activeLiveChatId,actualEndTime,scheduledStartTime))");
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument json;
            try { json = JsonDocument.Parse(body); }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            { throw RestError(response, default); }
            using var document = json;
            if (!response.IsSuccessStatusCode) throw RestError(response, json.RootElement);
            if (!json.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                throw new YouTubeChatException("not_found", "Трансляция не найдена или закрыта для доступа по ключу API. Проверьте ссылку.");
            var video = items[0];
            if (!video.TryGetProperty("liveStreamingDetails", out var live))
                throw new YouTubeChatException("not_live", "Это видео не является активной прямой трансляцией.");
            if (String(live, "actualEndTime").Length > 0)
                throw new YouTubeChatException("ended", "Трансляция уже завершена. Укажите ссылку на текущий эфир.");
            var chatId = String(live, "activeLiveChatId");
            if (chatId.Length == 0)
                throw new YouTubeChatException("no_chat", "Чат недоступен: эфир ещё не начался или чат отключён. Подключитесь после начала эфира.");
            video.TryGetProperty("snippet", out var snippet);
            return new(videoId, chatId, ChatText.Clean(String(snippet, "title"), 200),
                ChatText.Clean(String(snippet, "channelTitle"), 150));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new YouTubeChatException("timeout", "YouTube не ответил вовремя. Проверьте соединение.", true); }
        catch (HttpRequestException)
        { throw new YouTubeChatException("network", "Нет связи с YouTube API. Проверьте интернет и доступ к YouTube.", true); }
        catch (JsonException)
        { throw new YouTubeChatException("response", "YouTube вернул некорректный ответ. Попробуйте подключиться позднее.", true); }
    }

    public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string apiKey, string chatId, string? pageToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new LiveChatMessageListRequest { LiveChatId = chatId, Hl = "ru" };
        request.Part.Add(new[] { "id", "snippet", "authorDetails" });
        if (!string.IsNullOrEmpty(pageToken)) request.PageToken = pageToken;
        using var call = _client.StreamList(request,
            new Metadata { { "x-goog-api-key", YouTubeVideoLink.NormalizeKey(apiKey) } }, cancellationToken: cancellationToken);
        var first = true;
        while (await MoveNextAsync(call.ResponseStream, first, cancellationToken).ConfigureAwait(false))
        {
            first = false;
            var response = call.ResponseStream.Current;
            var messages = new List<YouTubeChatMessage>();
            var ended = response.OfflineAt.Length > 0;
            foreach (var item in response.Items)
            {
                var snippet = item.Snippet;
                if (snippet is null) continue;
                ended |= snippet.Type == 4; // CHAT_ENDED_EVENT
                if (item.Id.Length == 0 || snippet.Type is 2 or 4 ||
                    (snippet.HasHasDisplayContent && !snippet.HasDisplayContent)) continue;
                var text = ChatText.Clean(snippet.TextMessageDetails?.MessageText ?? snippet.DisplayMessage);
                if (text.Length == 0) continue;
                var author = item.AuthorDetails;
                DateTimeOffset? published = DateTimeOffset.TryParse(snippet.PublishedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date) ? date : null;
                messages.Add(new(item.Id, author?.ChannelId ?? snippet.AuthorChannelId,
                    ChatText.Clean(author?.DisplayName, 150), text, published, snippet.Type,
                    author?.IsChatOwner == true, author?.IsChatModerator == true));
            }
            yield return new(messages, response.NextPageToken, ended);
        }
    }

    private static async Task<bool> MoveNextAsync(IAsyncStreamReader<LiveChatMessageListResponse> reader,
        bool first, CancellationToken cancellationToken)
    {
        try
        {
            var next = reader.MoveNext(cancellationToken);
            return first ? await next.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false)
                : await next.ConfigureAwait(false);
        }
        catch (RpcException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (RpcException error) { throw RpcError(error); }
        catch (TimeoutException)
        { throw new YouTubeChatException("timeout", "YouTube не подтвердил подключение к чату.", true); }
    }

    internal static YouTubeChatException RpcError(RpcException error) => error.StatusCode switch
    {
        StatusCode.Unauthenticated or StatusCode.PermissionDenied => new("access",
            "Нет доступа к чату. Проверьте ключ, включение YouTube Data API v3 и ограничения ключа в Google Cloud."),
        StatusCode.ResourceExhausted => new("quota",
            "YouTube ограничил запросы или исчерпана квота проекта. Автоповтор остановлен; проверьте квоту в Google Cloud.", retryAfter: TimeSpan.FromMinutes(1)),
        StatusCode.FailedPrecondition => new("ended_or_disabled", "Чат завершён или отключён на YouTube."),
        StatusCode.NotFound => new("not_found", "Чат не найден. Проверьте ссылку на текущую трансляцию."),
        StatusCode.InvalidArgument => new("invalid_argument", "YouTube отклонил параметры чата. Подключитесь заново к текущему эфиру."),
        StatusCode.Unimplemented => new("unsupported", "Потоковый API чата недоступен. Проверьте доступ к YouTube API."),
        StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal or StatusCode.Cancelled =>
            new("network", "Связь с чатом YouTube прервана.", true),
        _ => new("stream", "Не удалось читать чат YouTube. Проверьте соединение и настройки API.")
    };

    internal static YouTubeChatException RestError(HttpResponseMessage response, JsonElement root)
    {
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var entry in errors.EnumerateArray()) reasons.Add(String(entry, "reason"));
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var entry in details.EnumerateArray()) reasons.Add(String(entry, "reason"));
        }
        if (reasons.Overlaps(["quotaExceeded", "dailyLimitExceeded", "DAILY_LIMIT_EXCEEDED"]))
            return new("quota", "Исчерпана квота YouTube API. Проверьте квоту проекта в Google Cloud и подключитесь после её восстановления.",
                retryAfter: TimeSpan.FromMinutes(1));
        if (response.StatusCode == HttpStatusCode.TooManyRequests || reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded"]))
        {
            var wait = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(1));
            if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
            return new("rate_limit", "YouTube временно ограничил запросы.", true, wait);
        }
        if ((int)response.StatusCode >= 500)
            return new("server", "YouTube API временно недоступен.", true);
        if (reasons.Overlaps(["accessNotConfigured", "SERVICE_DISABLED"]))
            return new("disabled", "Включите YouTube Data API v3 в проекте Google Cloud, где создан ключ.");
        return new("access", "YouTube отклонил запрос. Проверьте ключ API, его ограничения и доступность трансляции.");
    }

    private static string String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public void Dispose()
    {
        _channel?.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}

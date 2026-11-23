using System.Net;
using System.Text;
using System.Text.Json;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.Tests;

internal static class AnonymousYouTubeChatTests
{
    internal static async Task RunAsync()
    {
        var config = JsonSerializer.Serialize(new
        {
            INNERTUBE_CONTEXT = new { client = new { clientName = "WEB", clientVersion = "9.test" } },
            INNERTUBE_CLIENT_VERSION = "9.test", INNERTUBE_API_KEY = "public-web-config-key"
        });
        object Message(string id) => new
        {
            addChatItemAction = new { item = new { liveChatTextMessageRenderer = new
            {
                id, authorExternalChannelId = "UC-native-user", authorName = new { simpleText = "Viewer" },
                timestampUsec = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000).ToString(),
                message = new { runs = new[] { new { text = "Привет <literal> !test" } } },
                authorBadges = new[] { new { liveChatAuthorBadgeRenderer = new { icon = new { iconType = "MODERATOR" } } } }
            } } }
        };
        var chat = new { actions = new[] { Message("initial-id") },
            header = new { sortFilterSubMenuRenderer = new { subMenuItems = new[]
            {
                new { title = "Интересные сообщения", continuation = new { reloadContinuationData = new { continuation = "cursor-filtered" } } },
                new { title = "Чат", continuation = new { reloadContinuationData = new { continuation = "cursor-all" } } }
            } } },
            continuations = new[] { new { invalidationContinuationData = new { continuation = "cursor-initial", timeoutMs = 1 } } } };
        var data = JsonSerializer.Serialize(new { contents = new { liveChatRenderer = chat } });
        var html = "<script>ytcfg.set({not: valid});ytcfg.set(" + config + ");var ytInitialData = " + data +
            ";var ytInitialPlayerResponse = {\"playabilityStatus\":{\"status\":\"OK\"},\"videoDetails\":{\"title\":\"Title\",\"author\":\"Channel\"}};</script>";
        var allCursor = "all-view-" + new string('a', 120);
        var response = JsonSerializer.Serialize(new { continuationContents = new { liveChatContinuation = new
        {
            actions = new[] { Message("next-id") },
            header = new { sortFilterSubMenuRenderer = new { subMenuItems = new[]
            {
                new { title = "Top chat", continuation = new { reloadContinuationData = new { continuation = "filtered" } } },
                new { title = "Live chat", continuation = new { reloadContinuationData = new { continuation = allCursor } } }
            } } },
            continuations = new[] { new { timedContinuationData = new { continuation = "cursor-next", timeoutMs = 1000 } } }
        } } });
        var handler = new Handler(html, response, allCursor);
        using var http = new HttpClient(handler);
        using var transport = new AnonymousYouTubeChatTransport(http);
        Check(!transport.RequiresApiKey, "Guest reader requires a user key");
        var broadcast = await transport.ResolveAsync("", "abc123_-XYZ", default);
        Check(broadcast.VideoId == "abc123_-XYZ" && broadcast.Title == "Title", "Guest discovery failed");
        var batches = new List<YouTubeChatBatch>();
        await foreach (var batch in transport.ReadAsync("", broadcast.ChatId, null, default))
        { batches.Add(batch); if (batches.Count == 3) break; }
        Check(batches[0].Messages.Single().IsHistorical && batches[1].NextPageToken == allCursor &&
            batches[2].NextPageToken == "cursor-next", "History/full-chat cursor boundary lost");
        var message = batches[1].Messages.Single();
        Check(message.Id == "next-id" && message.AuthorChannelId == "UC-native-user" && message.IsModerator &&
            message.PublishedAt.HasValue && message.Text == "Привет <literal> !test", "Guest message identity or text lost");
        Check(handler.Requests == 3 && handler.SafeHeaders && handler.DynamicContext && handler.UsedAllCursor,
            "Guest reader used credentials, a hardcoded context or a short menu token as polling cursor");
        using var escaped = WebChatJson.Assignment("stub=1;ytcfg.set({\"test\":\"a } b \\\" c\"});", "ytcfg.set(", "test");
        Check(escaped!.RootElement.GetProperty("test").GetString() == "a } b \" c", "Balanced JSON parser broke quoted braces");
        using var blockedHttp = new HttpClient(new BlockedHandler());
        using var blocked = new AnonymousYouTubeChatTransport(blockedHttp);
        try { await blocked.ResolveAsync("", "abc123_-XYZ", default); throw new InvalidOperationException("Blocked guest request succeeded"); }
        catch (YouTubeChatException e) { Check(e.Code == "guest_blocked" && !e.Message.Contains("PRIVATE"), "Raw response escaped into UI"); }
        // The common session accepts an empty key for this transport.
        await using var session = new YouTubeChatSession(transport, "", "abc123_-XYZ");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler(string html, string response, string allCursor) : HttpMessageHandler
    {
        public int Requests;
        public bool SafeHeaders = true, DynamicContext, UsedAllCursor;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            SafeHeaders &= request.Headers.Authorization is null;
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                using var json = JsonDocument.Parse(body);
                var cursor = json.RootElement.GetProperty("continuation").GetString();
                UsedAllCursor |= cursor == allCursor;
                DynamicContext = json.RootElement.GetProperty("context").GetProperty("client").GetProperty("clientVersion").GetString() == "9.test" &&
                    (cursor == "cursor-initial" || cursor == allCursor) &&
                    request.Headers.GetValues("X-YouTube-Client-Version").Single() == "9.test" &&
                    request.RequestUri!.Query.Contains("public-web-config-key");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? html : response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class BlockedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("PRIVATE server response") });
    }
}

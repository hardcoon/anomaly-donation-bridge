using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AnomalyDonationBridge.ViewerChat;

// Public viewing endpoints, verified against VK Video Live on 22 November 2026.
// This is a guest reader, not the authenticated developer API. Guest WebSocket
// credentials stay in memory; no login, private channel or send/moderation API is used.
public sealed class VkVideoLiveChatTransport : IViewerChatTransport
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    public VkVideoLiveChatTransport(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = http is null;
    }

    public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
    {
        channel = ViewerChatLinks.Parse(ChatPlatform.VkVideoLive, channel);
        using var info = await GetJsonAsync($"/v1/blog/{channel}/public_video_stream", token).ConfigureAwait(false);
        var room = JsonValue.Text(info.RootElement, "wsChatChannel");
        if (!room.StartsWith("channel-chat:", StringComparison.Ordinal) || room.Length > 120)
            throw new ViewerChatException("Публичный чат этого канала VK Видео Live недоступен.", false);
        using var credential = await GetJsonAsync("/v1/ws/connect", token).ConfigureAwait(false);
        var guest = JsonValue.Text(credential.RootElement, "token");
        if (guest.Length == 0) throw new ViewerChatException("VK Видео Live не выдал гостевое подключение.");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://live.vkvideo.ru");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            await socket.ConnectAsync(new Uri("wss://pubsub.live.vkvideo.ru/connection/websocket?cf_protocol_version=v2"),
                deadline.Token).ConfigureAwait(false);
            await ChatSocket.SendAsync(socket, JsonSerializer.Serialize(new { id = 1, connect = new { token = guest, name = "adb-viewer-chat" } }),
                deadline.Token).ConfigureAwait(false);
            await WaitForReplyAsync(socket, 1, channel, publish, deadline.Token).ConfigureAwait(false);
            await ChatSocket.SendAsync(socket, JsonSerializer.Serialize(new { id = 2, subscribe = new { channel = room } }),
                deadline.Token).ConfigureAwait(false);
            await WaitForReplyAsync(socket, 2, channel, publish, deadline.Token).ConfigureAwait(false);
        }
        // Subscribe before requesting history so live messages queue on the socket
        // while its initial REST snapshot is read. Repeated IDs are merged by the feed.
        connected();
        try
        {
            using var history = await GetJsonAsync($"/v1/blog/{channel}/public_video_stream/chat", token).ConfigureAwait(false);
            if (history.RootElement.TryGetProperty("data", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Reverse())
                    if (ParseMessage(channel, message, historical: true) is { } parsed)
                        await publish(new(ChatEventKind.Message, parsed), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or JsonException or ViewerChatException or OperationCanceledException)
        { /* History is optional; a failed history request must not stop live chat. */ }

        while (!token.IsCancellationRequested)
        {
            var frame = await ChatSocket.ReadAsync(socket, token).ConfigureAwait(false);
            await HandleFrameAsync(socket, frame, channel, publish, token).ConfigureAwait(false);
        }
    }

    private static async Task WaitForReplyAsync(ClientWebSocket socket, int id, string channel,
        Func<ViewerChatEvent, CancellationToken, Task> publish, CancellationToken token)
    {
        while (true)
        {
            var frame = await ChatSocket.ReadAsync(socket, token).ConfigureAwait(false);
            var received = false;
            foreach (var line in frame.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var replyId) && replyId.TryGetInt32(out var value) && value == id)
                {
                    if (doc.RootElement.TryGetProperty("error", out _))
                        throw new ViewerChatException("VK Видео Live отклонил гостевое подключение.");
                    received = true;
                }
            }
            await HandleFrameAsync(socket, frame, channel, publish, token).ConfigureAwait(false);
            if (received) return;
        }
    }

    private static async Task HandleFrameAsync(ClientWebSocket socket, string frame, string channel,
        Func<ViewerChatEvent, CancellationToken, Task> publish, CancellationToken token)
    {
        foreach (var line in frame.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Trim() == "{}")
            { await ChatSocket.SendAsync(socket, "{}", token).ConfigureAwait(false); continue; }
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("push", out var push))
            {
                if (push.TryGetProperty("disconnect", out _) || push.TryGetProperty("unsubscribe", out _))
                    throw new ViewerChatException("VK Видео Live обновляет соединение.");
                if (push.TryGetProperty("pub", out var publication) && publication.TryGetProperty("data", out var data) &&
                    ParsePublication(channel, data) is { } change) await publish(change, token).ConfigureAwait(false);
            }
        }
    }

    internal static ViewerChatEvent? ParsePublication(string channel, JsonElement publication)
    {
        var type = JsonValue.Text(publication, "type");
        if (!publication.TryGetProperty("data", out var data)) return null;
        if (type == "message")
            return ParseMessage(channel, data, historical: false) is { } message ? new(ChatEventKind.Message, message) : null;
        if (type is "delete_message" or "message_delete" or "remove_message")
            return new(ChatEventKind.DeleteMessage, Id: JsonValue.Text(data, "id"));
        if (type == "clean_chat_messages")
        {
            var user = JsonValue.Text(data, "userId");
            return new(user.Length == 0 ? ChatEventKind.ClearChannel : ChatEventKind.ClearUser, UserId: user);
        }
        return null;
    }

    internal static ViewerMessage? ParseMessage(string channel, JsonElement item, bool historical)
    {
        if (item.ValueKind != JsonValueKind.Object || JsonValue.Flag(item, "isDeleted") ||
            !item.TryGetProperty("author", out var author)) return null;
        var id = JsonValue.Text(item, "id");
        if (id.Length == 0) return null;
        var name = JsonValue.Text(author, "displayName");
        if (name.Length == 0) name = JsonValue.Text(author, "name");
        var text = new StringBuilder();
        if (item.TryGetProperty("data", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                var type = JsonValue.Text(part, "type");
                var content = JsonValue.Text(part, "content");
                if (type == "text")
                {
                    try
                    {
                        using var encoded = JsonDocument.Parse(content);
                        text.Append(encoded.RootElement.ValueKind == JsonValueKind.Array && encoded.RootElement.GetArrayLength() > 0
                            ? encoded.RootElement[0].GetString() : content);
                    }
                    catch (JsonException) { text.Append(content); }
                }
                else if (type is "smile" or "emote" or "sticker" or "image") text.Append(" [смайл] ");
                if (text.Length >= 4000) break;
            }
        }
        DateTimeOffset? at = null;
        if (long.TryParse(JsonValue.Text(item, "createdAt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            try { at = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        return new(ChatPlatform.VkVideoLive, channel, id, JsonValue.Text(author, "id"), name,
            text.ToString(), at, historical || at is null, JsonValue.Flag(author, "isOwner"),
            JsonValue.Flag(author, "isChatModerator") || JsonValue.Flag(author, "isChannelModerator"),
            item.TryGetProperty("flags", out var flags) && JsonValue.Flag(flags, "isTwitchMessage"));
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.live.vkvideo.ru" + path));
        request.Headers.Add("X-From-Id", Guid.NewGuid().ToString("D"));
        request.Headers.UserAgent.ParseAdd("AnomalyDonationBridge/1.4.10");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ViewerChatException("Канал VK Видео Live не найден.", false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new ViewerChatException("Этот чат VK Видео Live недоступен для публичного просмотра.", false);
        if (!response.IsSuccessStatusCode) throw new ViewerChatException("VK Видео Live временно недоступен.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > 2_097_152) throw new ViewerChatException("Слишком большой ответ VK Видео Live.");
        }
        return JsonDocument.Parse(output.ToArray());
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

internal static class JsonValue
{
    internal static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" :
                value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "" : "";
    internal static bool Flag(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

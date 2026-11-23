using System.Text.Json;
using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.Tests;

internal static class ViewerChatTests
{
    internal static async Task RunAsync()
    {
        Check(ViewerChatLinks.Parse(ChatPlatform.Twitch, "https://www.twitch.tv/Test_Channel") == "test_channel", "Twitch channel parsing");
        Check(ViewerChatLinks.Parse(ChatPlatform.VkVideoLive, "https://vkplay.live/thewolfstalker") == "thewolfstalker", "Old VK alias");
        Check(ViewerChatLinks.Parse(ChatPlatform.VkVideoLive, "https://live.vkvideo.ru/test/record/record-id") == "test", "Recording must resolve only its live channel");
        foreach (var bad in new[] { "https://twitch.tv.attacker.test/test", "https://twitch.tv@attacker.test/test",
                     "https://twitch.tv:8443/test", "file:///test", "https://twitch.tv/test/videos", "test\r\nJOIN #other" })
        {
            try { ViewerChatLinks.Parse(ChatPlatform.Twitch, bad); throw new InvalidOperationException("Unsafe channel accepted"); }
            catch (ViewerChatException) { }
        }
        var irc = IrcLine.Parse("@badge-info=;badges=moderator/1;display-name=Test\\sUser;id=msg-1;mod=1;tmi-sent-ts=1795373272000;user-id=55 :test!test@test.tmi.twitch.tv PRIVMSG #room :Привет: !команда");
        var parsed = TwitchChatTransport.ParseEvent("room", irc)!.Message!;
        Check(parsed.Author == "Test User" && parsed.Id == "msg-1" && parsed.UserId == "55" &&
            parsed.IsModerator && parsed.Text == "Привет: !команда" && parsed.PublishedAt.HasValue, "Twitch native identity/tags/text");
        Check(TwitchChatTransport.ParseEvent("different-room", irc) is null, "Cross-channel message escaped");
        Check(TwitchChatTransport.ParseEvent("room", IrcLine.Parse(":a!b PRIVMSG #room :no id")) is null, "Missing native message ID accepted");
        Check(TwitchChatTransport.ParseEvent("room", IrcLine.Parse("@target-msg-id=msg-1 :tmi.twitch.tv CLEARMSG #room :text")) is
            { Kind: ChatEventKind.DeleteMessage, Id: "msg-1" }, "Twitch message removal");
        Check(TwitchChatTransport.ParseEvent("room", IrcLine.Parse("@target-user-id=55 :tmi.twitch.tv CLEARCHAT #room :user")) is
            { Kind: ChatEventKind.ClearUser, UserId: "55" }, "Twitch per-user removal");
        var vkJson = JsonSerializer.Serialize(new
        {
            type = "message",
            data = new { id = 123, createdAt = 1795373272, author = new { id = 55, displayName = "Зритель", isOwner = true },
                data = new[] { new { type = "text", content = JsonSerializer.Serialize(new object[] { "Привет <script> literal", "unstyled", Array.Empty<object>() }) } } }
        });
        using (var vk = JsonDocument.Parse(vkJson))
        {
            var message = VkVideoLiveChatTransport.ParsePublication("room", vk.RootElement)!.Message!;
            Check(message.Id == "123" && message.UserId == "55" && message.IsOwner &&
                message.Text == "Привет <script> literal", "VK public message format");
            Check(VkVideoLiveChatTransport.ParseMessage("room", vk.RootElement.GetProperty("data"), true)!.IsHistorical,
                "VK history lost its boundary flag");
        }
        var feed = new ViewerChatFeed(4, 8);
        var youtube = new ViewerMessage(ChatPlatform.YouTube, "room", "same-id", "u", "Name", "message", DateTimeOffset.UtcNow);
        feed.Publish(youtube); feed.Publish(youtube);
        feed.Publish(youtube with { Platform = ChatPlatform.Twitch });
        feed.Publish(youtube with { ChannelId = "other" });
        Check(feed.Snapshot.Messages.Count == 3 && feed.Snapshot.ReceivedCount == 3, "Dedup is not scoped by platform/channel");
        Check(ReferenceEquals(feed.Snapshot, feed.Snapshot), "Unchanged snapshot allocates/can repaint UI");
        feed.Apply(ChatPlatform.Twitch, "room", new(ChatEventKind.DeleteMessage, Id: "same-id"));
        Check(feed.Snapshot.Messages.Count == 2 && feed.Snapshot.Messages.All(e => e.Message.Platform != ChatPlatform.Twitch), "Delete removed unrelated platform");
        feed.Clear(); feed.Publish(youtube);
        Check(feed.Snapshot.Messages.Count == 0, "Clear caused historical replay");
        feed.Apply(ChatPlatform.YouTube, "room", new(ChatEventKind.DeleteMessage, Id: "not-arrived"));
        feed.Publish(youtube with { Id = "not-arrived" });
        Check(feed.Snapshot.Messages.Count == 0, "Deleted message reappeared from late history");
        var concurrent = new ViewerChatFeed();
        Parallel.For(0, 12000, i => concurrent.Publish(youtube with { Id = i.ToString() }));
        Check(concurrent.Snapshot.Messages.Count == 1500 && concurrent.Snapshot.ReceivedCount == 12000, "Feed grew without a bound or lost concurrent publications");
        Check(concurrent.Snapshot.Messages.Zip(concurrent.Snapshot.Messages.Skip(1)).All(p => p.First.Sequence < p.Second.Sequence), "Mixed-source arrival order corrupted");
        var transport = new FakeTransport();
        await using (var session = new ViewerChatSession(transport, "room", (e, _) =>
                     { concurrent.Apply(ChatPlatform.Twitch, "room", e); return Task.CompletedTask; }))
        {
            session.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (session.Snapshot.State != ViewerChatState.Connected) await Task.Delay(10, timeout.Token);
        }
        Check(transport.Cancelled && transport.Disposed, "Disconnect leaked the reader");
        var retryFeed = new ViewerChatFeed();
        var retryReader = new RetryReader();
        await using (var retrySession = new ViewerChatSession(retryReader, "room", (e, _) =>
                     { retryFeed.Apply(ChatPlatform.Twitch, "room", e); return Task.CompletedTask; }))
        {
            retrySession.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (retrySession.Snapshot.State != ViewerChatState.Connected || retryReader.Attempts < 2)
                await Task.Delay(10, timeout.Token);
            Check(retryFeed.Snapshot.ReceivedCount == 1, "Reconnect replay duplicated a native message");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeTransport : IViewerChatTransport
    {
        public bool Cancelled, Disposed;
        public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
        {
            connected();
            try { await Task.Delay(Timeout.Infinite, token); } finally { Cancelled = token.IsCancellationRequested; }
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class RetryReader : IViewerChatTransport
    {
        public int Attempts;
        public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
        {
            var attempt = Interlocked.Increment(ref Attempts);
            await publish(new(ChatEventKind.Message, new(ChatPlatform.Twitch, channel, "stable-id", "u", "User", "text", DateTimeOffset.UtcNow)), token);
            if (attempt == 1) throw new ViewerChatException("Temporary test disconnect");
            connected();
            await Task.Delay(Timeout.Infinite, token);
        }
        public void Dispose() { }
    }
}

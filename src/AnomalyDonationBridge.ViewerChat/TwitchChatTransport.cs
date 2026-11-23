using System.Globalization;
using System.Net.WebSockets;

namespace AnomalyDonationBridge.ViewerChat;

// Read-only public IRC connection. It never sends PRIVMSG or uses an account token.
public sealed class TwitchChatTransport : IViewerChatTransport
{
    public async Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token)
    {
        channel = ViewerChatLinks.Parse(ChatPlatform.Twitch, channel);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            await socket.ConnectAsync(new Uri("wss://irc-ws.chat.twitch.tv"), timeout.Token).ConfigureAwait(false);
        }
        foreach (var command in new[] { "CAP REQ :twitch.tv/tags twitch.tv/commands", "PASS SCHMOOPIIE",
                     "NICK justinfan" + Random.Shared.Next(100000, 999999), "JOIN #" + channel })
            await ChatSocket.SendAsync(socket, command, token).ConfigureAwait(false);
        var ready = false;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(30));
        while (!token.IsCancellationRequested)
        {
            var frame = await ChatSocket.ReadAsync(socket, ready ? token : handshake.Token).ConfigureAwait(false);
            foreach (var line in frame.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = IrcLine.Parse(line.TrimEnd('\r'));
                if (parsed.Command == "PING")
                    await ChatSocket.SendAsync(socket, "PONG :" + parsed.Parameters.LastOrDefault(), token).ConfigureAwait(false);
                else if (parsed.Command == "RECONNECT") throw new ViewerChatException("Twitch обновляет соединение.");
                else if (parsed.Command == "ROOMSTATE" && parsed.Parameters.FirstOrDefault() == "#" + channel)
                { if (!ready) { ready = true; connected(); } }
                else if (parsed.Command == "NOTICE" && (line.Contains("Login authentication failed", StringComparison.Ordinal) ||
                         parsed.Tags.GetValueOrDefault("msg-id") == "msg_channel_suspended"))
                    throw new ViewerChatException("Twitch отклонил подключение. Проверьте канал.", false);
                else if (ParseEvent(channel, parsed) is { } item) await publish(item, token).ConfigureAwait(false);
            }
        }
    }

    internal static ViewerChatEvent? ParseEvent(string channel, IrcLine line)
    {
        if (line.Parameters.FirstOrDefault() != "#" + channel) return null;
        if (line.Command == "CLEARMSG")
            return new(ChatEventKind.DeleteMessage, Id: line.Tags.GetValueOrDefault("target-msg-id", ""));
        if (line.Command == "CLEARCHAT")
        {
            var id = line.Tags.GetValueOrDefault("target-user-id", "");
            return new(id.Length == 0 ? ChatEventKind.ClearChannel : ChatEventKind.ClearUser, UserId: id);
        }
        if (line.Command != "PRIVMSG" || line.Parameters.Count < 2) return null;
        var messageId = line.Tags.GetValueOrDefault("id", "");
        if (messageId.Length == 0) return null;
        DateTimeOffset? at = null;
        if (long.TryParse(line.Tags.GetValueOrDefault("tmi-sent-ts"), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            try { at = DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch (ArgumentOutOfRangeException) { }
        var badges = line.Tags.GetValueOrDefault("badges", "");
        var author = line.Tags.GetValueOrDefault("display-name", "");
        if (author.Length == 0) author = line.Prefix.Split('!')[0];
        return new(ChatEventKind.Message, new(ChatPlatform.Twitch, channel, messageId,
            line.Tags.GetValueOrDefault("user-id", ""), author, line.Parameters[1], at,
            IsHistorical: at is null, IsOwner: badges.Contains("broadcaster/", StringComparison.Ordinal),
            IsModerator: line.Tags.GetValueOrDefault("mod") == "1"));
    }
    public void Dispose() { }
}

internal sealed record IrcLine(string Prefix, string Command, IReadOnlyList<string> Parameters,
    Dictionary<string, string> Tags)
{
    internal static IrcLine Parse(string line)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (line.StartsWith('@'))
        {
            var end = line.IndexOf(' ');
            if (end < 0) return new("", "", [], tags);
            foreach (var pair in line[1..end].Split(';'))
            {
                var equals = pair.IndexOf('=');
                if (equals > 0) tags[pair[..equals]] = Unescape(pair[(equals + 1)..]);
            }
            line = line[(end + 1)..];
        }
        var prefix = "";
        if (line.StartsWith(':'))
        {
            var end = line.IndexOf(' ');
            if (end < 0) return new("", "", [], tags);
            prefix = line[1..end]; line = line[(end + 1)..];
        }
        var args = new List<string>();
        var trailing = line.IndexOf(" :", StringComparison.Ordinal);
        var head = trailing < 0 ? line : line[..trailing];
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new(prefix, "", [], tags);
        args.AddRange(parts.Skip(1));
        if (trailing >= 0) args.Add(line[(trailing + 2)..]);
        return new(prefix, parts[0], args, tags);
    }
    private static string Unescape(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
                output.Append(value[++i] switch { 's' => ' ', ':' => ';', 'r' => '\r', 'n' => '\n', var c => c });
            else output.Append(value[i]);
        }
        return output.ToString();
    }
}

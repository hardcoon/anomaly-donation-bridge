using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.ViewerChat;

public enum ChatPlatform { YouTube, Twitch, VkVideoLive }
public enum ViewerChatState { Disconnected, Connecting, Connected, Retrying, Error }
public enum ChatEventKind { Message, DeleteMessage, ClearUser, ClearChannel }

public sealed record ViewerMessage(ChatPlatform Platform, string ChannelId, string Id, string UserId,
    string Author, string Text, DateTimeOffset? PublishedAt, bool IsHistorical = false,
    bool IsOwner = false, bool IsModerator = false, bool IsMirrored = false);
public sealed record ViewerChatEntry(long Sequence, ViewerMessage Message);
public sealed record ViewerChatSnapshot(long Revision, IReadOnlyList<ViewerChatEntry> Messages, long ReceivedCount);
public sealed record ViewerConnectionSnapshot(ViewerChatState State, string Status, string Channel = "");
public sealed record ViewerChatEvent(ChatEventKind Kind, ViewerMessage? Message = null,
    string Id = "", string UserId = "");

public interface IViewerChatTransport : IDisposable
{
    Task RunAsync(string channel, Func<ViewerChatEvent, CancellationToken, Task> publish, Action connected, CancellationToken token);
}

public sealed class ViewerChatException(string message, bool retryable = true) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

public static partial class ViewerChatLinks
{
    public static string Parse(ChatPlatform platform, string? value)
    {
        if (platform is not (ChatPlatform.Twitch or ChatPlatform.VkVideoLive))
            throw new ArgumentOutOfRangeException(nameof(platform));
        var input = (value ?? "").Trim();
        if (input.Contains("://") || input.Contains('/'))
        {
            if (!input.Contains("://")) input = "https://" + input;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
                throw Invalid(platform);
            var host = uri.IdnHost.ToLowerInvariant();
            var allowed = platform == ChatPlatform.Twitch
                ? host is "twitch.tv" or "www.twitch.tv" or "m.twitch.tv"
                : host is "live.vkvideo.ru" or "vkplay.live" or "live.vkplay.ru";
            if (!allowed || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Invalid(platform);
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            // A recording link identifies its channel; never subscribe to recording replay.
            if (parts.Length != 1 && !(platform == ChatPlatform.VkVideoLive &&
                    parts.Length == 3 && parts[1] == "record")) throw Invalid(platform);
            input = parts[0];
        }
        if (platform == ChatPlatform.Twitch ? !TwitchName().IsMatch(input) : !VkName().IsMatch(input))
            throw Invalid(platform);
        return input.ToLowerInvariant();
    }

    private static ViewerChatException Invalid(ChatPlatform platform) => new(platform == ChatPlatform.Twitch
        ? "Укажите имя канала Twitch или ссылку https://twitch.tv/канал."
        : "Укажите канал VK Видео Live: https://live.vkvideo.ru/канал.", false);
    [GeneratedRegex("^[a-zA-Z0-9_]{1,25}$", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchName();
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex VkName();
}

internal static class ViewerText
{
    internal static string Clean(string? value, int limit = 4000)
    {
        var text = new string((value ?? "").Take(limit).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }
}

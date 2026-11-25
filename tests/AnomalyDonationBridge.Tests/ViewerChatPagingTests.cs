using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.Tests;

internal static class ViewerChatPagingTests
{
    internal static Task RunAsync()
    {
        var feed = new ViewerChatFeed();
        var pages = new ViewerChatPageWindow();
        void Publish(int i) => feed.Publish(new(ChatPlatform.Twitch, "room", "m-" + i, "u", "Name", "Message " + i, DateTimeOffset.UtcNow));
        for (var i = 1; i <= 700; i++) Publish(i);
        var latest = pages.Select(feed.Snapshot, true);
        Check(latest.Count == 150 && latest[0].Sequence == 551 && latest[^1].Sequence == 700, "Initial window is not the latest 150");
        var older = pages.LoadOlder(feed.Snapshot);
        Check(older.Count == 300 && older[0].Sequence == 401 && ReferenceEquals(older[^1], latest[^1]), "History is not prepended by 150");
        Publish(701);
        Check(pages.Select(feed.Snapshot, false).Count == 300 && pages.HasNewer(feed.Snapshot), "New messages disturbed a paused reader");
        feed.Apply(ChatPlatform.Twitch, "room", new(ChatEventKind.DeleteMessage, Id: "m-450"));
        Check(pages.Select(feed.Snapshot, false).All(item => item.Message.Id != "m-450"), "Deleted message remains in a loaded page");
        while (pages.HasOlder(feed.Snapshot)) older = pages.LoadOlder(feed.Snapshot);
        Check(older.Count == 699 && older[0].Sequence == 1 && older[^1].Sequence == 700, "All retained history cannot be paged");
        var independent = new ViewerChatPageWindow();
        Check(independent.Select(feed.Snapshot, true).Count == 150 && pages.Select(feed.Snapshot, false).Count == 699,
            "Detached windows share browsing positions");
        for (var i = 702; i <= 100000; i++) Publish(i);
        var retained = pages.Select(feed.Snapshot, false);
        Check(feed.Snapshot.Messages.Count == 1500 && retained.Count == 150 && retained[0].Sequence == 98501,
            "Long chat grows unbounded or a reader jumps straight to the live end");
        Check(pages.Select(feed.Snapshot, true).Count == 150 && !pages.HasNewer(feed.Snapshot), "Returning to live did not restore a single page");
        feed.Clear(); Check(pages.Select(feed.Snapshot, false).Count == 0, "Clear left loaded history");
        Publish(100001); Check(pages.Select(feed.Snapshot, false).Count == 1, "Clear did not reset paging");
        return Task.CompletedTask;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}

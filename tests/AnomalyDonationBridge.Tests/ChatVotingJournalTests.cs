using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.Tests;

internal static class ChatVotingJournalTests
{
    internal static async Task RunAsync()
    {
        await DurabilityAndCheckpoint();
        await RecoveryAndCorruption();
        await MigrationBackup();
        await PeriodicCheckpoint();
    }

    private static AppPaths NewPaths() => new(Path.Combine(
        Environment.GetEnvironmentVariable("ADB_TEST_ROOT") ?? throw new InvalidOperationException("ADB_TEST_ROOT is required"),
        "chat-journal-" + Guid.NewGuid().ToString("N")));
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-25T12:00:00Z");
    private static ChatVotingMessage Message(string id, DateTimeOffset? at = null) => ChatVotingFloodTests.Message(id, "u-" + id, "!да", at ?? Start);
    private static void Check(bool condition, string message) => ChatVotingFloodTests.Check(condition, message);
    private static async Task ExpectInvalid(Func<Task> work)
    {
        try { await work(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Corrupt accounting was not rejected");
    }

    private static async Task DurabilityAndCheckpoint()
    {
        var paths = NewPaths(); var store = new JsonVotingStore(paths);
        var engine = await ChatVotingFloodTests.Create(store, () => Start);
        var id = engine.Snapshot.Id;
        var snapshot = await File.ReadAllBytesAsync(store.FilePath);
        var plain = Enumerable.Range(0, 10000).Select(i => Message("plain-" + i) with { Text = "обычный чат" }).ToArray();
        await engine.RecordChatAsync(id, "profile", plain);
        Check(!File.Exists(store.JournalPath) && snapshot.SequenceEqual(await File.ReadAllBytesAsync(store.FilePath)), "Ordinary chat touched disk");
        Check(await engine.RecordChatAsync(id, "profile", [Message("first")]) == 1, "Durable vote missing");
        Check(File.Exists(store.JournalPath) && snapshot.SequenceEqual(await File.ReadAllBytesAsync(store.FilePath)),
            "A chat vote rewrote the complete voting archive");
        var restartedStore = new JsonVotingStore(paths);
        var restored = new VotingCoordinator(restartedStore, () => Start); await restored.InitializeAsync();
        Check(restored.Snapshot.Total == 1 && restored.Snapshot.ChatRevision == 1 &&
            await restored.RecordChatAsync(id, "profile", [Message("first")]) == 0, "Restart lost or counted a journal vote twice");
        var before = await File.ReadAllBytesAsync(store.JournalPath);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await restored.RecordChatAsync(id, "profile", [Message("cancel")], cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        Check(before.SequenceEqual(await File.ReadAllBytesAsync(store.JournalPath)) && restored.Snapshot.Total == 1,
            "Cancellation published or persisted a vote");
        using (var locked = new FileStream(store.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { await restored.RecordChatAsync(id, "profile", [Message("retry")]); throw new InvalidOperationException("Locked journal write should fail"); }
            catch (IOException) { }
            Check(restored.Snapshot.Total == 1 && restored.Snapshot.ChatRevision == 1, "Failed append changed live accounting");
        }
        Check(await restored.RecordChatAsync(id, "profile", [Message("retry")]) == 1, "Disk failure consumed a message ID");
        var oldJournal = await File.ReadAllBytesAsync(store.JournalPath);
        var settings = restored.Snapshot; settings.Title = "После контрольной точки";
        await restored.SaveSettingsAsync(settings);
        Check(!File.Exists(store.JournalPath), "Full save did not checkpoint the journal");
        // Crash after replacement but before deletion of the old WAL.
        await File.WriteAllBytesAsync(store.JournalPath, oldJournal);
        var loaded = await new JsonVotingStore(paths).LoadAsync();
        Check(loaded.Current.Total == 2 && loaded.Current.Title == settings.Title, "Checkpoint replay duplicated old votes");
        // A Windows reader can prevent WAL deletion while snapshot replacement succeeds.
        using (var locked = new FileStream(store.JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            settings = restored.Snapshot; settings.Title = "Заблокирована очистка журнала";
            await restored.SaveSettingsAsync(settings);
            Check(await restored.RecordChatAsync(id, "profile", [Message("during-cleanup")]) == 1,
                "Committed snapshot was reported as failed because journal cleanup was blocked");
        }
        loaded = await new JsonVotingStore(paths).LoadAsync();
        Check(loaded.Current.Total == 3, "Blocked cleanup lost an acknowledged vote");
        // Old journal entries cannot resurrect a removed collection.
        loaded.Current = new VotingRound(); loaded.OtherCollections.Clear(); loaded.PreviousRounds.Clear();
        await new JsonVotingStore(paths).SaveAsync(loaded);
        await File.WriteAllBytesAsync(store.JournalPath, oldJournal);
        Check((await new JsonVotingStore(paths).LoadAsync()).Current.Total == 0, "Checkpoint resurrected a removed round");
    }

    private static async Task RecoveryAndCorruption()
    {
        var paths = NewPaths(); var store = new JsonVotingStore(paths);
        var engine = await ChatVotingFloodTests.Create(store, () => Start);
        var id = engine.Snapshot.Id;
        await engine.RecordChatAsync(id, "profile", [Message("one"), Message("two")]);
        var valid = await File.ReadAllBytesAsync(store.JournalPath);
        await using (var partial = new FileStream(store.JournalPath, FileMode.Append, FileAccess.Write))
            await partial.WriteAsync(Encoding.ASCII.GetBytes("{\"incomplete\":"));
        Check((await new JsonVotingStore(paths).LoadAsync()).Current.Total == 2 &&
            valid.SequenceEqual(await File.ReadAllBytesAsync(store.JournalPath)), "Incomplete tail did not recover exactly the durable prefix");
        var entry = JsonNode.Parse(Encoding.UTF8.GetString(valid).TrimEnd('\n'))!;
        entry["sha256"] = new string('0', 64);
        var damaged = Encoding.UTF8.GetBytes(entry.ToJsonString() + "\n");
        await File.WriteAllBytesAsync(store.JournalPath, damaged);
        var originalSnapshot = await File.ReadAllBytesAsync(store.FilePath);
        await ExpectInvalid(() => new JsonVotingStore(paths).LoadAsync());
        Check(damaged.SequenceEqual(await File.ReadAllBytesAsync(store.JournalPath)) &&
            originalSnapshot.SequenceEqual(await File.ReadAllBytesAsync(store.FilePath)), "Corruption silently rewrote or reset accounting");
        entry = JsonNode.Parse(Encoding.UTF8.GetString(valid).TrimEnd('\n'))!;
        var payload = JsonNode.Parse(Convert.FromBase64String(entry["payload"]!.GetValue<string>()))!;
        payload["previousRevision"] = 5; payload["revision"] = 6;
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        entry["payload"] = Convert.ToBase64String(payloadBytes);
        entry["sha256"] = Convert.ToHexString(SHA256.HashData(payloadBytes));
        await File.WriteAllTextAsync(store.JournalPath, entry.ToJsonString() + "\n", new UTF8Encoding(false));
        await ExpectInvalid(() => new JsonVotingStore(paths).LoadAsync());
        await File.WriteAllBytesAsync(store.JournalPath, valid);
        var wrongBase = JsonNode.Parse(originalSnapshot)!;
        wrongBase["chatJournalId"] = "";
        await File.WriteAllTextAsync(store.FilePath, wrongBase.ToJsonString(), new UTF8Encoding(false));
        await ExpectInvalid(() => new JsonVotingStore(paths).LoadAsync());
        await File.WriteAllBytesAsync(store.FilePath, originalSnapshot);
        File.Move(store.FilePath, store.FilePath + ".missing");
        await ExpectInvalid(() => new JsonVotingStore(paths).LoadAsync());
        File.Move(store.FilePath + ".missing", store.FilePath);
        Check((await new JsonVotingStore(paths).LoadAsync()).Current.Total == 2, "Recovery checks damaged valid data");
    }

    private static async Task MigrationBackup()
    {
        var paths = NewPaths(); var store = new JsonVotingStore(paths);
        var engine = await ChatVotingFloodTests.Create(store, () => Start);
        var old = await store.LoadAsync(); old.SchemaVersion = 7;
        old.Current.SeenYouTubeMessageIds.UnionWith(Enumerable.Range(0, 10000).Select(i => "legacy-" + i));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(old, JsonDefaults.Options);
        await File.WriteAllBytesAsync(store.FilePath, bytes);
        var migrated = new VotingCoordinator(new JsonVotingStore(paths), () => Start); await migrated.InitializeAsync();
        Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(store.MigrationBackupPath)) &&
            (await new JsonVotingStore(paths).LoadAsync()).SchemaVersion == 8 &&
            migrated.Snapshot.SeenYouTubeMessageIds.Count <= VotingChatReplay.Capacity, "Migration lacks an exact legacy backup or bounded IDs");
        await new JsonVotingStore(paths).LoadAsync();
        Check(bytes.SequenceEqual(await File.ReadAllBytesAsync(store.MigrationBackupPath)), "Later loads overwrote the legacy migration backup");
    }

    private static async Task PeriodicCheckpoint()
    {
        var paths = NewPaths(); var store = new JsonVotingStore(paths);
        var engine = await ChatVotingFloodTests.Create(store, () => Start);
        var id = engine.Snapshot.Id;
        var before = await File.ReadAllBytesAsync(store.FilePath);
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 4097; i++)
            Check(await engine.RecordChatAsync(id, "profile", [Message("checkpoint-" + i)]) == 1, "Checkpoint lost an incoming vote");
        Check(!before.SequenceEqual(await File.ReadAllBytesAsync(store.FilePath)) && !File.Exists(store.JournalPath),
            "Journal was never checkpointed or checkpoint did not consume its final transaction");
        var loaded = await new JsonVotingStore(paths).LoadAsync();
        Check(loaded.Current.Total == 4097 && loaded.Current.YouTubeParticipants.Count == 4097 &&
            loaded.Current.SeenYouTubeMessageIds.Count <= VotingChatReplay.Capacity,
            "Periodic checkpoint lost voter identity or replay limits");
        timer.Stop();
        Console.WriteLine($"  4097 separately flushed votes + checkpoint + reload: {timer.ElapsedMilliseconds} ms; WAL bounded.");
    }
}

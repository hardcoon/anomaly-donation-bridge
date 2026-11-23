using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Tests;

internal static class VotingGenerationTests
{
    internal static async Task RunAsync()
    {
        var settings = new VotingGenerationSettings { Enabled = true };
        var names = new[] { "БТР атакует", "Напарник с именем донатчика", "Случайный телепорт", "Странствующий торговец", "Синий трактор 2" };
        var commands = VotingGeneration.Commands(names, settings);
        Check(commands.SequenceEqual(["!БА", "!НСИД", "!СТ", "!СТ2", "!СТ22"]), "Unique initials, including natural numeric suffix collision");
        settings.Prefix = ""; settings.Format = VotingCommandFormat.Number;
        Check(VotingGeneration.Commands(names, settings).SequenceEqual(["1", "2", "3", "4", "5"]), "Prefix is optional and numbers start at one");
        settings.Prefix = "#"; settings.Format = VotingCommandFormat.Initials;
        var repeated = VotingGeneration.Commands(Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("Я", 120)), 99).ToArray(), settings);
        Check(repeated.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 99 && repeated.All(code => code.Length <= 80), "Long titles or 99 acronym collisions broke uniqueness");
        var active = Enumerable.Range(0, 120).Select(i => Template("Активный шаблон " + i)).ToList();
        var custom = Enumerable.Range(0, 14).Select(i => Template("Пользовательский шаблон " + i)).ToList();
        foreach (var quantity in new[] { 2, 99 })
        {
            settings.Count = quantity;
            var generated = VotingGeneration.Generate(settings, active, _ => 0);
            Check(generated.Count == quantity && generated.Select(option => option.Preset!.Id).Distinct().Count() == quantity,
                "Generation duplicated templates or ignored quantity bounds");
            Check(generated.All(option => option.Amount == 0 && option.Preset!.Validate().Count == 0), "Generated invalid actions or money");
        }
        settings.Count = 2; settings.ActiveTemplateIds = [];
        await Throws(() => Task.FromResult(VotingGeneration.Generate(settings, active)), "Empty selection silently used every template");

        var store = new Store(); var now = DateTimeOffset.Parse("2026-11-23T08:00:00Z"); long rolls = 0;
        var engine = new VotingCoordinator(store, () => now, maximum => rolls++ % maximum,
            (_, source) => source == VotingTemplateSource.Active ? active : custom);
        await engine.InitializeAsync(); await engine.EnterProfileAsync("profile");
        var first = engine.Snapshot;
        first.Generation.Enabled = true; first.Generation.Count = 2;
        first.Generation.ActiveTemplateIds = active.Take(7).Select(item => item.Id).ToHashSet();
        first.TimeGoal = true; first.MoneyGoal = false; first.DurationSeconds = 10;
        await engine.SaveSettingsAsync(first);
        Check(engine.Snapshot.Options.Count == 0 && engine.CanStartCollection(engine.Snapshot), "Saving configuration generated entries before start");
        var secondId = await engine.AddCollectionAsync(profileId: "profile");
        var second = engine.SnapshotFor(secondId);
        second.Generation = new() { Enabled = true, Source = VotingTemplateSource.UserPresets, Count = 10, Prefix = "#", Format = VotingCommandFormat.Number };
        second.TimeGoal = true; second.MoneyGoal = false; second.DurationSeconds = 10;
        await engine.SaveSettingsAsync(second);
        await engine.SetCollectionActiveAsync(first.CollectionId, true, "profile");
        var started = engine.SnapshotFor(first.CollectionId);
        Check(started.Phase == VotingPhase.Collecting && started.Options.Count == 2 &&
            started.Options.All(option => first.Generation.ActiveTemplateIds.Contains(option.Preset!.Id)), "Start ignored the source or multi-select");
        var firstEntries = started.Options.Select(option => option.Id).ToArray();
        await engine.RecordAsync(new DonationEvent { Id = "money", Amount = 3, Currency = "RUB", Message = started.Options[0].Command, CreatedAt = now }, "profile");
        await engine.SetCollectionActiveAsync(first.CollectionId, false, "profile");
        var pauseRolls = rolls; now = now.AddSeconds(2);
        await engine.SetCollectionActiveAsync(first.CollectionId, true, "profile");
        Check(engine.SnapshotFor(first.CollectionId).Options.Select(option => option.Id).SequenceEqual(firstEntries) &&
            engine.SnapshotFor(first.CollectionId).Total == 3 && rolls == pauseRolls, "Resume regenerated entries or lost existing votes");
        await engine.SetCollectionActiveAsync(secondId, true, "profile");
        var secondStarted = engine.SnapshotFor(secondId);
        Check(secondStarted.Options.Count == 10 && secondStarted.Options.All(option => custom.Any(item => item.Id == option.Preset!.Id)) &&
            secondStarted.Options.Select(option => option.Command).SequenceEqual(Enumerable.Range(1, 10).Select(i => "#" + i)),
            "Separate vote did not use its own source, quantity or formatting");
        var staleEditor = engine.SnapshotFor(secondId);
        now = now.AddSeconds(11); await engine.TickAsync();
        await engine.SetCollectionActiveAsync(secondId, false, "profile");
        var before = rolls; await engine.SetCollectionActiveAsync(secondId, true, "profile");
        var secondRestarted = engine.SnapshotFor(secondId);
        Check(secondRestarted.Id != secondStarted.Id && rolls > before && secondRestarted.Options.Count == 10 &&
            !secondRestarted.Options.Select(option => option.Id).Intersect(secondStarted.Options.Select(option => option.Id)).Any(),
            "New launch reused an old round or its entries");
        await Throws(() => engine.SaveSettingsAsync(staleEditor), "Stale editor overwrote a new generated round");
        var editor = engine.SnapshotFor(secondId);
        editor.Generation.Count = 3; editor.Options.Clear(); await engine.SaveSettingsAsync(editor);
        Check(engine.SnapshotFor(secondId).Options.Count == 10 && engine.SnapshotFor(secondId).Generation.Count == 3,
            "Editing future settings changed the current generated list");
        var restored = new VotingCoordinator(store, () => now, _ => throw new InvalidOperationException("Unexpected reroll on restore"),
            (_, source) => source == VotingTemplateSource.Active ? active : custom);
        await restored.InitializeAsync();
        Check(restored.SnapshotFor(secondId).Options.Select(option => option.Id).SequenceEqual(secondRestarted.Options.Select(option => option.Id)),
            "Restart replaced a saved generated round");
        now = now.AddSeconds(11); await engine.TickAsync();
        await engine.SetSequenceAsync([first.CollectionId, secondId], new HashSet<string> { secondId }, profileId: "profile");
        await engine.StartNextCollectionAsync("profile");
        Check(engine.SnapshotFor(secondId).Options.Count == 3 && engine.SnapshotFor(secondId).Phase == VotingPhase.Collecting,
            "Automatic queue transition did not generate a fresh list with updated settings");
        var atomic = engine.SnapshotFor(secondId);
        var unavailableId = await engine.AddCollectionAsync(profileId: "profile");
        var invalid = engine.SnapshotFor(unavailableId);
        invalid.Generation = new() { Enabled = true, Count = 99, ActiveTemplateIds = active.Take(7).Select(item => item.Id).ToHashSet() };
        await engine.SaveSettingsAsync(invalid);
        await Throws(() => engine.SetCollectionActiveAsync(unavailableId, true, "profile"), "Too few selected templates accepted");
        Check(engine.SnapshotFor(secondId).IsActive && engine.SnapshotFor(secondId).Id == atomic.Id, "Failed start paused another vote");
        now = now.AddSeconds(11); await engine.TickAsync();
        await engine.SetCollectionActiveAsync(secondId, false, "profile");
        var savedId = engine.SnapshotFor(secondId).Id; store.FailNext = true;
        await Throws(() => engine.SetCollectionActiveAsync(secondId, true, "profile"), "Disk failure did not reject generation");
        Check(engine.SnapshotFor(secondId).Id == savedId && engine.SnapshotFor(secondId).Phase != VotingPhase.Collecting,
            "Unpersisted generation became visible");
        var copySettings = engine.SnapshotFor(secondId); copySettings.Generation.UserTemplateIds = custom.Take(5).Select(item => item.Id).ToHashSet();
        await engine.SaveSettingsAsync(copySettings);
        var map = custom.ToDictionary(item => item.Id, _ => ActionDefinition.NewInstanceId());
        await engine.CopyProfileAsync("profile", "copy", templateIdMap: map);
        var copied = engine.CollectionsFor("copy").Single(round => round.Title == copySettings.Title);
        Check(copied.Generation.Count == 3 && copied.Generation.UserTemplateIds!.SetEquals(custom.Take(5).Select(item => map[item.Id])) &&
            !ReferenceEquals(copied.Generation, engine.SnapshotFor(secondId).Generation), "Profile copy did not preserve independent mapped selections");
    }

    private static ActionDefinition Template(string name) => new() { DisplayName = name, HandlerId = "add_radiation", IsActive = true };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> operation, string message)
    { try { await operation(); } catch (InvalidOperationException) { return; } catch (IOException) { return; } throw new InvalidOperationException(message); }
    private sealed class Store : IVotingStore
    {
        private VotingDocument _document = new();
        public bool FailNext;
        public Task<VotingDocument> LoadAsync(CancellationToken token = default) => Task.FromResult(Copy(_document));
        public Task SaveAsync(VotingDocument document, CancellationToken token = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("Isolated disk failure"); }
            _document = Copy(document); return Task.CompletedTask;
        }
        private static VotingDocument Copy(VotingDocument document) => JsonSerializer.Deserialize<VotingDocument>(JsonSerializer.Serialize(document))!;
    }
}

using System.IO;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class SafeZoneTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        var settings = new AppSettings();
        Check(!SettingsNormalizer.Normalize(settings).SafeZone.Enabled, "Old settings must keep safe zone disabled.");
        settings.SafeZone = new SafeZoneSettings
        {
            Enabled = true, BlockedAction = SafeZoneBlockedAction.Wait,
            Rules = [new() { LevelName = "l01_escape", DisplayName = "Кордон", BlockAll = false, HandlerIds = ["change_money"] }]
        };
        var snapshot = SettingsNormalizer.CreatePersistenceSnapshot(settings);
        snapshot.SafeZone.Rules[0].HandlerIds.Add("spawn_mutants");
        Check(settings.SafeZone.Rules[0].HandlerIds.Count == 1, "Persistence projection aliases live rules.");
        var store = new JsonSettingsStore(paths);
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Check(loaded.SafeZone.Enabled && loaded.SafeZone.BlockedAction == SafeZoneBlockedAction.Wait &&
              loaded.SafeZone.Rules.Single().HandlerIds.Single() == "change_money", "Safe-zone JSON roundtrip lost rules.");
        var normalized = SafeZoneSettings.Normalize(new SafeZoneSettings
        {
            Rules = [new() { LevelName = "L01_ESCAPE", BlockAll = false, HandlerIds = ["change_money"] },
                new() { LevelName = "l01_escape", BlockAll = true, HandlerIds = ["spawn_mutants"] }]
        });
        Check(normalized.Rules.Count == 1 && normalized.Rules[0].BlockAll && normalized.Rules[0].HandlerIds.Count == 2,
            "Duplicate locations weakened protection.");
        try
        {
            SafeZoneConfig.Serialize(new SafeZoneSettings { Rules = [new() { LevelName = "x]\n[injected" }] });
            throw new InvalidOperationException("Unsafe LTX location was accepted.");
        }
        catch (InvalidDataException) { }
        var game = Path.Combine(temporary.Path, "mock-game"); Directory.CreateDirectory(game);
        await SafeZoneConfig.WriteAsync(game, settings.SafeZone);
        var configPath = Path.Combine(game, SafeZoneConfig.RelativePath);
        var content = await File.ReadAllTextAsync(configPath);
        Check(content.Contains("[level_l01_escape]") && content.Contains("change_money = true") &&
              content.Contains("value = " + SafeZoneConfig.Revision(settings.SafeZone)) && !content.Contains("Кордон"),
            "Generated LTX lost exact level/handler IDs or contains localized identifiers.");
        var unchanged = File.GetLastWriteTimeUtc(configPath);
        await SafeZoneConfig.WriteAsync(game, settings.SafeZone);
        Check(File.GetLastWriteTimeUtc(configPath) == unchanged, "Unchanged rules rewrote the game file.");
        var command = new GameCommand { ActionId = "change_money", DonationId = "test" };
        Check(SafeZoneConfig.Prepare(command, CapabilitySet.Empty, settings.SafeZone, true).IsTransient,
            "An older addon bypassed safety capability gating.");
        var capabilities = CapabilitySet.Create(EngineProfile.Anomaly153Vanilla, "test", [SafeZoneConfig.Capability], ["change_money"]);
        Check(SafeZoneConfig.Prepare(command, capabilities, settings.SafeZone, true).IsValid && command.RequireSafeZoneCheck,
            "Supported addon cannot receive guarded commands.");
        var wire = PipeProtocol.Command(command).Split('\t');
        Check(wire.Length == 14 && wire[13] == SafeZoneConfig.Revision(settings.SafeZone) && command.Parameters.Count == 0,
            "Safety guard leaked into effect parameters or lost exact configuration revision.");
        command.SourceHandlerId = "random_active";
        Check(PipeProtocol.Command(command).Split('\t') is { Length: 15 } sourceWire && sourceWire[14] == "random_active",
            "Random template source was lost on the guarded wire.");
        Check(SafeZoneConfig.Prepare(command, capabilities, settings.SafeZone, false).IsTransient, "Unsynchronized config can execute.");
        settings.SafeZone.Enabled = false;
        Check(SafeZoneConfig.Prepare(command, CapabilitySet.Empty, settings.SafeZone, false).IsValid &&
              !command.RequireSafeZoneCheck && PipeProtocol.Command(command).Split('\t').Length == 7,
            "Disabling safety left stale guard metadata on a queued command.");

        var editor = new SafeZoneEditorViewModel(settings.SafeZone,
            [Level("l01_escape", "Кордон"), Level("l05_bar", "Бар")],
            [Effect("change_money", "Деньги"), Effect("spawn_mutants", "Мутанты")]);
        Check(editor.Rules.Count == 1 && editor.AvailableLocations.Single().LevelName == "l05_bar", "Picker duplicates configured locations.");
        editor.Enabled = true;
        editor.AddLocationCommand.Execute(null);
        Check(editor.SelectedRule is { BlockAll: true, LevelName: "l05_bar" } && editor.Rules.Count == 2,
            "Adding a scanned location did not default to blocking all effects.");
        editor.SelectedRule!.BlockSelected = true;
        editor.SelectedRule.Groups.SelectMany(group => group.Effects).Single(effect => effect.HandlerId == "spawn_mutants").IsSelected = true;
        var edited = editor.ToSettings();
        Check(edited.Rules.Single(rule => rule.LevelName == "l05_bar").HandlerIds.Single() == "spawn_mutants" &&
              settings.SafeZone.Rules.Count == 1, "Editing rules mutates live settings before Save.");
        editor.RemoveLocationCommand.Execute(null);
        Check(editor.Rules.Count == 1 && editor.AvailableLocations.Count == 1, "Removing a rule lost its catalog location.");
        var missing = new SafeZoneEditorViewModel(new SafeZoneSettings
        { Rules = [new() { LevelName = "removed_map", HandlerIds = ["vendor.missing.effect"], BlockAll = false }] }, [], []);
        Check(missing.ToSettings().Rules.Single().HandlerIds.Single() == "vendor.missing.effect", "A missing mod silently lost a protection rule.");
        Check(!editor.IsAddingLocation && editor.HasRules, "Compact editor has the wrong initial state.");
        editor.BeginAddLocationCommand.Execute(null);
        Check(editor.IsAddingLocation, "Inline location picker did not open.");
        editor.CancelAddLocationCommand.Execute(null);
        Check(!editor.IsAddingLocation && editor.Rules.Count == 1, "Cancelling the location picker changed rules.");
        editor.AddLocationCommand.Execute(null);
        var firstRule = editor.Rules[0]; var secondRule = editor.Rules[1];
        Check(secondRule.IsExpanded && !firstRule.IsExpanded && !editor.IsAddingLocation, "New rule does not open exclusively.");
        editor.ToggleRuleCommand.Execute(firstRule);
        Check(firstRule.IsExpanded && !secondRule.IsExpanded && editor.SelectedRule == firstRule, "Opening a row failed to collapse the previous editor.");
        editor.ToggleRuleCommand.Execute(firstRule);
        Check(editor.SelectedRule is null && !firstRule.IsExpanded, "Rule cannot be collapsed.");
        editor.ToggleRuleCommand.Execute(secondRule);
        editor.RemoveRuleCommand.Execute(firstRule);
        Check(editor.SelectedRule == secondRule && secondRule.IsExpanded && editor.Rules.Count == 1,
            "Removing a closed row changed the open rule.");
        editor.RemoveRuleCommand.Execute(secondRule);
        Check(!editor.HasRules && editor.SelectedRule is null && editor.AvailableLocations.Count == 2,
            "Deleting the last compact rule left a ghost selection or lost catalog locations.");

        await DispatcherAsync();
    }

    internal static CatalogEntry Level(string name, string display) => new()
    { Kind = CatalogEntryKind.Level, SectionId = "adb_level_" + name, Faction = name, DisplayName = display, IsAvailable = true };
    private static ActionDefinition Effect(string id, string name) => new()
    { Id = id, HandlerId = id, DisplayName = name, CategoryId = "npc_mutants", CooldownSeconds = 0 };

    private static async Task DispatcherAsync()
    {
        var history = new InMemoryHistorySink();
        var dispatcher = new DonationDispatcher(new InMemoryProcessedDonationStore(), history) { GlobalCooldown = TimeSpan.Zero };
        var transport = new Transport();
        var policy = new QueuePolicy();
        var action = Effect("change_money", "Деньги");
        await dispatcher.EnqueueActionAsync(new DonationEvent { Id = "safe-skip", Amount = 1, Currency = "RUB" }, action, policy);
        await dispatcher.DrainAsync(transport, policy);
        var skipped = await dispatcher.CompleteAsync(new GameExecutionResult
        { CommandId = transport.Commands.Last().CommandId, Status = GameResultStatus.Rejected, Reason = "safe_zone_blocked:l01_escape" });
        Check(skipped is { State: DispatchState.Skipped, CanRetryManually: true } && skipped.Detail.Contains("Безопасная зона"),
            "Skipped protection was recorded as execution or cannot be manually repeated.");
        await dispatcher.DrainAsync(transport, policy);
        Check(transport.Commands.Count == 1, "Skipped effect was automatically retried.");
        await dispatcher.EnqueueActionAsync(new DonationEvent { Id = "safe-wait", Amount = 1, Currency = "RUB" }, action, policy);
        await dispatcher.DrainAsync(transport, policy);
        var waitingId = transport.Commands.Last().CommandId;
        await dispatcher.CompleteAsync(new GameExecutionResult
        { CommandId = waitingId, Status = GameResultStatus.Deferred, Reason = "safe_zone_waiting:l01_escape" });
        await dispatcher.DrainAsync(transport, policy);
        Check(transport.Commands.Count == 3 && transport.Commands.Last().CommandId == waitingId, "Wait mode lost or duplicated the queued command.");
        await dispatcher.CompleteAsync(new GameExecutionResult { CommandId = waitingId, Status = GameResultStatus.Executed });
        await dispatcher.DrainAsync(transport, policy);
        Check(transport.Commands.Count == 3, "Completed wait-mode command repeated again.");

        using var temporary = new Program.TemporaryDirectory();
        var pendingStore = new JsonPendingDispatchStore(new AppPaths(temporary.Path));
        var ledger = new InMemoryProcessedDonationStore();
        var randomDispatcher = new DonationDispatcher(ledger, new InMemoryHistorySink(), pendingStore: pendingStore);
        action.IsActive = true;
        await randomDispatcher.EnqueueActionAsync(new DonationEvent { Id = "random-source", Amount = 1, Currency = "RUB" },
            Effect("random_active", "Случайный активный"), policy, availableActions: [action]);
        var pending = (await pendingStore.LoadAsync()).Single();
        Check(pending.Command.SourceHandlerId == "random_active", "Resolved random handler lost its source before sending.");
        pending.Command.SourceHandlerId = string.Empty; // old queue records still contain SourceAction
        pending.Command.RequireSafeZoneCheck = true; pending.Command.SafeZoneRevision = new string('a', 64);
        await pendingStore.SaveAsync([pending]);
        var restored = new DonationDispatcher(ledger, new InMemoryHistorySink(), pendingStore: pendingStore);
        Check(await restored.RestoreAsync() == 1, "Protected command was lost on app restart.");
        var restoredTransport = new Transport(); await restored.DrainAsync(restoredTransport, policy);
        Check(restoredTransport.Commands.Single() is { SourceHandlerId: "random_active", RequireSafeZoneCheck: true } saved &&
              saved.SafeZoneRevision == new string('a',64), "Durable queue lost guard or random-template provenance.");
    }
    private sealed class Transport : IGameCommandTransport
    {
        public bool IsConnected => true;
        public bool IsGameReady => true;
        public List<GameCommand> Commands { get; } = [];
        public Task SendAsync(GameCommand command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return Task.CompletedTask; }
    }
}

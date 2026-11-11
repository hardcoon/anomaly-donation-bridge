using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class GlobalHotkeyTests
{
    public static async Task RunAsync()
    {
        Check(GlobalHotkeyGesture.TryNormalize("Shift+Alt+F10", out var first, out _) &&
            first == "Alt+Shift+F10", "Modifier order was not normalized.");
        Check(GlobalHotkeyGesture.TryNormalize("Ctrl+Alt+Shift+Win+D7", out var allModifiers, out _) &&
            allModifiers == "Ctrl+Alt+Shift+Win+D7", "A four-modifier hotkey was rejected.");
        Check(GlobalHotkeyGesture.TryNormalize("Space", out var noModifier, out _) && noModifier == "Space",
            "A valid single-key global hotkey was rejected.");
        Check(!GlobalHotkeyGesture.TryNormalize("Ctrl+Shift", out _, out _),
            "A modifier-only combination was accepted.");
        Check(!GlobalHotkeyGesture.TryNormalize("Ctrl+A+B", out _, out _),
            "A combination with two primary keys was accepted.");

        var settings = new AppSettings
        {
            ToggleDonationProcessingHotkey = "Ctrl+Alt+F9",
            RepeatLastDonationHotkey = "Shift+F10",
            ActivePresetHotkeys =
            [
                new ActivePresetHotkey { Id = "preset-hotkey-1", PresetNumber = 2, Hotkey = "Ctrl+F8" }
            ],
            FortuneWheelIdleMinutes = 15,
            EffectMo2ModPath = @"C:\Mods\ADB",
            AnonymousDonorName = "Таинственный зритель"
        };
        var snapshot = SettingsNormalizer.CreatePersistenceSnapshot(settings);
        Check(snapshot.ToggleDonationProcessingHotkey == "Ctrl+Alt+F9" &&
            snapshot.RepeatLastDonationHotkey == "Shift+F10" &&
            snapshot.ActivePresetHotkeys.Count == 1 &&
            snapshot.ActivePresetHotkeys[0].PresetNumber == 2 &&
            snapshot.ActivePresetHotkeys[0].Hotkey == "Ctrl+F8" &&
            snapshot.FortuneWheelIdleMinutes == 15 &&
            snapshot.EffectMo2ModPath == settings.EffectMo2ModPath &&
            snapshot.AnonymousDonorName == "Таинственный зритель",
            "Hotkey, fortune-wheel or MO2 settings disappeared from the persistence snapshot.");

        using var temporary = new Program.TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppPaths(temporary.Path));
        var viewModel = new MainViewModel(runtime)
        {
            SelectedHistory = new EventHistoryEntry
            {
                ActionId = "already-completed-action",
                CanRetryManually = false,
                State = DispatchState.Executed
            }
        };
        Check(viewModel.RetryCommand.CanExecute(null),
            "Repeat remained disabled for an already completed history entry.");

        var olderSuccess = new EventHistoryEntry
        {
            DonationId = "donation-success", State = DispatchState.Executed,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var latestRejected = new EventHistoryEntry
        {
            DonationId = "donation-rejected", State = DispatchState.Rejected,
            Timestamp = DateTimeOffset.UtcNow
        };
        var ignoredManual = new EventHistoryEntry
        {
            DonationId = "manual:donation-rejected:copy", State = DispatchState.Executed,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        Check(ReferenceEquals(BridgeRuntime.SelectLastDonationForRepeat(
                [olderSuccess, latestRejected, ignoredManual]), latestRejected),
            "The hotkey did not select the latest real donation regardless of its result.");

        var donorEntry = new EventHistoryEntry
        {
            Donor = "OriginalDonor",
            DonationMessage = "Original message"
        };
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "spawn_named_companion") == "OriginalDonor",
            "A manually repeated companion lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "hardcoon.adb.mutant_companion_v2") == "OriginalDonor",
            "A manually repeated V2 mutant lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "change_player_name") == "OriginalDonor",
            "A manually repeated player-name change lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "hardcoon.adb.rename_nearest_npc") == "OriginalDonor",
            "A manually repeated NPC-name change lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "hardcoon.adb.annoying_fan") == "OriginalDonor",
            "A manually repeated annoying fan lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "hardcoon.adb.donor_thief") == "OriginalDonor",
            "A manually repeated donor thief lost the original donor name.");
        Check(BridgeRuntime.ManualRetryDonor(
                new EventHistoryEntry(), "hardcoon.adb.donor_thief", "Таинственный зритель") ==
              "Таинственный зритель",
            "An anonymous donor thief retry ignored the configured replacement.");
        Check(BridgeRuntime.ManualRetryDonor(donorEntry, "change_health") == "Ручной повтор",
            "A regular manual retry did not use the manual-repeat label.");
        Check(BridgeRuntime.ManualRetryDonor(
                new EventHistoryEntry(), "spawn_named_companion", "Таинственный зритель") ==
              "Таинственный зритель",
            "A manually repeated anonymous donor-named effect ignored the configured replacement.");
        Check(BridgeRuntime.ManualRetryMessage(
                donorEntry, "hardcoon.adb.annoying_fan") == "Original message",
            "A manually repeated annoying fan lost the original donation message.");
        Check(BridgeRuntime.ManualRetryMessage(donorEntry, "change_health") == string.Empty,
            "A regular manual retry unexpectedly exposed the donation message.");
        Check(BridgeRuntime.IntegrationAccountStatus("DonationAlerts", "  hardcoon  ") ==
              "DonationAlerts: hardcoon",
            "DonationAlerts status omitted the integration name or account nickname.");
        Check(BridgeRuntime.IntegrationAccountStatus("DonateX", "hard\ncoon") ==
              "DonateX: hard coon",
            "DonateX status did not safely normalize the account nickname.");
        Check(BridgeRuntime.CanArmIntegrations(false, false, true),
            "A DonateX-only setup was incorrectly tied to DonationAlerts.");
        Check(!BridgeRuntime.CanArmIntegrations(false, true, false),
            "Donation processing was enabled without any configured integration.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Collections.ObjectModel;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class SequenceEditorTests
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        ActionDefinition Make(string id, decimal amount) => new()
        { Id = id, HandlerId = "change_money", DisplayName = id, IsActive = true,
            Triggers = [new() { Amount = amount, Currency = "RUB" }] };
        var a = Make("a", 31); var other = Make("other", 99); var b = Make("b", 31);
        var profile = new DonationProfile { Name = "Test", Actions = [a, other, b],
            ActivePresetHotkeys = [new() { PresetNumber = 1, Hotkey = "F6" }] };
        runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
        runtime.Settings.Actions = profile.Actions; runtime.Settings.ActivePresetHotkeys = profile.ActivePresetHotkeys;
        var key = ActionPriceOrdering.GroupKey(a)!;
        await runtime.SetGroupStrictSequenceAsync(key, true);
        await runtime.SetGroupDelayAsync(key, 7);
        Check(a.GroupDelaySeconds == 7 && b.GroupDelaySeconds == 7 && other.GroupDelaySeconds is null,
            "Group pause changed an unrelated action or missed a member.");
        await runtime.ReorderActiveGroupAsync(key, "b", "a", false);
        Check(runtime.Settings.Actions.Select(action => action.Id).SequenceEqual(["b", "other", "a"]), "Group reorder changed other slots.");
        Check(runtime.Settings.ActivePresetHotkeys.Single().PresetNumber == 3, "Reorder redirected the hotkey to another action.");
        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Check(loaded.Actions.Select(action => action.Id).SequenceEqual(["b", "other", "a"]) &&
            loaded.Actions.Where(action => action.Id is "a" or "b").All(action => action.StrictSequence && action.GroupDelaySeconds == 7), "Order, pause or strict mode did not survive settings reload.");
        var browser = new ActionBrowserViewModel(new ObservableCollection<ActionViewModel>(loaded.Actions.Select(action => new ActionViewModel(action))), true);
        var group = browser.VisibleEntries.Single(entry => entry.IsGroup).Group!;
        Check(group.StrictSequence && group.GroupDelaySeconds == 7 && group.Actions.Select(action => action.SequenceOrdinal).SequenceEqual([1, 2]), "Step numbers, pause or group mode are missing.");
        foreach (var invalid in new[] { -1, 86_401 })
        {
            try { await runtime.SetGroupDelayAsync(key, invalid); throw new Exception("Invalid group pause was accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        try { await runtime.ReorderActiveGroupAsync(key, "b", "other", true); throw new Exception("Cross-group move was accepted."); }
        catch (InvalidOperationException) { }
        Check(runtime.Settings.Actions.Select(action => action.Id).SequenceEqual(["b", "other", "a"]), "Rejected drop changed the saved order.");
    }
}

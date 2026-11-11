using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    private readonly SemaphoreSlim _sequenceEditGate = new(1, 1);

    public async Task ReorderActiveGroupAsync(string key, string sourceId, string targetId, bool after)
    {
        await _sequenceEditGate.WaitAsync();
        var previous = Settings.Actions;
        var hotkeys = Settings.ActivePresetHotkeys.Select(binding => binding.PresetNumber).ToArray();
        try
        {
            var reordered = ActionPriceOrdering.MoveWithinGroup(previous, key, sourceId, targetId, after);
            // Keep hotkeys attached to the same instance when its ordinal moves.
            for (var index = 0; index < hotkeys.Length; index++)
            {
                var oldIndex = hotkeys[index] - 1;
                if (oldIndex >= 0 && oldIndex < previous.Count)
                    Settings.ActivePresetHotkeys[index].PresetNumber = reordered.FindIndex(action => action.Id == previous[oldIndex].Id) + 1;
            }
            Settings.Actions = reordered;
            await SaveSettingsAsync();
        }
        catch
        {
            Settings.Actions = previous;
            for (var index = 0; index < hotkeys.Length; index++) Settings.ActivePresetHotkeys[index].PresetNumber = hotkeys[index];
            SettingsChanged?.Invoke();
            throw;
        }
        finally { _sequenceEditGate.Release(); }
    }

    public async Task SetGroupStrictSequenceAsync(string key, bool enabled)
    {
        await _sequenceEditGate.WaitAsync();
        var members = Settings.Actions.Where(action => ActionPriceOrdering.GroupKey(action) == key).ToList();
        var previous = members.Select(action => action.StrictSequence).ToArray();
        try
        {
            foreach (var action in members) action.StrictSequence = enabled;
            await SaveSettingsAsync();
        }
        catch
        {
            for (var index = 0; index < members.Count; index++) members[index].StrictSequence = previous[index];
            SettingsChanged?.Invoke();
            throw;
        }
        finally { _sequenceEditGate.Release(); }
    }

    public async Task SetGroupDelayAsync(string key, int seconds)
    {
        if (seconds is < 0 or > 86_400)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Задержка должна быть от 0 до 86 400 секунд.");
        await _sequenceEditGate.WaitAsync();
        var members = Settings.Actions.Where(action => ActionPriceOrdering.GroupKey(action) == key).ToList();
        var previous = members.Select(action => action.GroupDelaySeconds).ToArray();
        try
        {
            foreach (var action in members) action.GroupDelaySeconds = seconds;
            await SaveSettingsAsync();
        }
        catch
        {
            for (var index = 0; index < members.Count; index++) members[index].GroupDelaySeconds = previous[index];
            SettingsChanged?.Invoke();
            throw;
        }
        finally { _sequenceEditGate.Release(); }
    }
}

using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.App.Services;

public sealed partial class BridgeRuntime
{
    internal IReadOnlyList<ActionDefinition> VotingTemplates(string profileId, VotingTemplateSource source)
    {
        var profile = Settings.Profiles.FirstOrDefault(item => item.Id == profileId);
        var actions = profileId == Settings.ActiveProfileId ? Settings.Actions : profile?.Actions;
        var presets = profileId == Settings.ActiveProfileId ? Settings.UserPresets : profile?.UserPresets;
        return source == VotingTemplateSource.Active ? (actions ?? []).Where(action => action.IsActive).ToArray() :
            (presets ?? []).ToArray();
    }
}

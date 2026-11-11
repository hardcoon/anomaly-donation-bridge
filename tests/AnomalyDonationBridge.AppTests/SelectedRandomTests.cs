using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class SelectedRandomTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        var presets = DefaultActionCatalog.CreatePresets();
        var a = presets.Single(p => p.HandlerId == "change_money").CloneAsNewInstance();
        var b = a.CloneAsNewInstance();
        a.DisplayName = b.DisplayName = "Одинаковое имя";
        a.Parameters["amount"] = "123"; b.Parameters["amount"] = "456";
        var random = presets.Single(p => p.HandlerId == ActiveTemplateSelection.HandlerId).CloneAsNewInstance();
        var otherRandom = presets.Single(p => p.HandlerId == DonationDispatcher.RandomActiveHandlerId).CloneAsNewInstance();
        var profile = new DonationProfile { Name = "Test", Actions = [a, b, random, otherRandom] };
        runtime.Settings.Profiles.Add(profile); runtime.Settings.ActiveProfileId = profile.Id;
        runtime.Settings.Actions = profile.Actions;

        var source = new ActionViewModel(random);
        var editor = new ActionEditorViewModel(source, async draft =>
        {
            source.ApplyEditorDraft(draft);
            await runtime.SaveActionParametersAsync(source.Id, draft.Model);
        }, activeTemplates: runtime.Settings.Actions);
        var selection = editor.Draft.Parameters.Single();
        Check(selection.IsActiveTemplateSelection && !selection.IsWrappedMultiChoice && selection.MultiChoiceOptions.Count == 2,
            "Editor must list configured instances and exclude nested randoms.");
        Check(selection.MultiChoiceOptions.Select(o => o.Label).Distinct().Count() == 2,
            "Same-named instances cannot be distinguished.");
        selection.MultiChoiceOptions.Single(o => o.Value == b.Id).IsSelected = true;
        Check(random.Parameters[ActiveTemplateSelection.ParameterKey] == string.Empty, "Unsaved draft changed the running selector.");
        Check(await editor.SaveAsync(), "Selection failed to save: " + editor.Error);
        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        var saved = loaded.Actions.Single(p => p.HandlerId == ActiveTemplateSelection.HandlerId);
        Check(saved.Parameters[ActiveTemplateSelection.ParameterKey] == b.Id, "Instance reference lost on persistence.");
        Check(saved.ParameterDefinitions.Single().Options.Count == 0, "Runtime labels leaked into portable settings.");
        new ActionManifestLoader().ApplyPresetsToInstances([saved], presets);
        Check(saved.Parameters[ActiveTemplateSelection.ParameterKey] == b.Id, "Preset schema refresh discarded opaque instance ID.");
        var reopened = new ActionEditorViewModel(new ActionViewModel(saved), _ => Task.CompletedTask, activeTemplates: [b, a]);
        Check(reopened.Draft.Parameters.Single().MultiChoiceOptions.Single(o => o.Value == b.Id).IsSelected,
            "Reordering changed the selection.");
        reopened.Draft.Parameters.Single().Value = string.Empty;
        Check(!await reopened.SaveAsync(), "Empty selection can be accidentally saved.");
        var otherProfile = new ActionEditorViewModel(new ActionViewModel(saved), _ => Task.CompletedTask, activeTemplates: [a.CloneAsNewInstance()]);
        Check(otherProfile.Draft.Parameters.Single().MultiChoiceOptions.Count(o => o.IsSelected) == 1 &&
              otherProfile.Draft.Parameters.Single().MultiChoiceOptions.Single(o => o.IsSelected).Label.Contains("Недоступный"),
            "Profile switch substituted an unrelated same-name/same-handler instance.");
        var legacy = random.Clone();
        legacy.Parameters.Clear(); legacy.Parameters["included_presets"] = "change_money";
        new ActionManifestLoader().ApplyPresetsToInstances([legacy], presets);
        Check(legacy.DisplayName == ActiveTemplateSelection.DisplayName &&
              legacy.Parameters[ActiveTemplateSelection.ParameterKey] == string.Empty && legacy.Validate().Count > 0,
            "Old random parameters must require explicit instance selection after upgrade.");
    }
}

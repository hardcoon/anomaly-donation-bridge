using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class SearchQueueVisibilityTests
{
    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        await using var runtime = new BridgeRuntime(new AppPaths(temporary.Path));
        var vm = new MainViewModel(runtime); // No InitializeAsync / real game IPC.
        foreach (var list in new[] { vm.ActiveActions, vm.UserPresets, vm.Presets })
        {
            list.Add(new(new ActionDefinition { Id = Guid.NewGuid().ToString("N"), HandlerId = "actor_boost", DisplayName = "Скорость героя", IsActive = true }));
            list.Add(new(new ActionDefinition { Id = Guid.NewGuid().ToString("N"), HandlerId = "change_money", DisplayName = "Изменить деньги", IsActive = true }));
        }
        void Queue(string value) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.QueueStatusText))!.SetValue(vm, value);
        foreach (var section in new[] { 0, 1, 2 })
        {
            vm.SelectedSectionIndex = section; vm.TemplateSearchText = "скорость";
            Queue("Следующий эффект через 00:20 · в очереди: 1");
            Check(vm.ShowTemplateSearch && vm.TemplateSearchText == "скорость",
                "A donation/countdown hid the typed search.");
            vm.TemplateSearchText = "no such template 138953";
            var browser = section switch { 0 => vm.ActiveBrowser, 1 => vm.UserPresetBrowser, _ => vm.PresetBrowser };
            Check(browser.VisibleActions.IsEmpty, "The typed query stopped filtering during the countdown.");
            Queue("Следующий эффект через 00:19 · в очереди: 2");
            Check(vm.ShowTemplateSearch, "A countdown tick hid the search.");
            vm.ClearTemplateSearch();
            Check(vm.TemplateSearchText == "" && vm.ShowTemplateSearch && browser.VisibleActions.Cast<object>().Count() == 2,
                "The clear action hid the field or left a stale filter.");
            Queue(string.Empty);
            Check(vm.ShowTemplateSearch, "Countdown completion hid the search.");
        }
    }

    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

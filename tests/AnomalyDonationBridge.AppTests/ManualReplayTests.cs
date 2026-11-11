using System.IO;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class ManualReplayTests
{
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    internal static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths);
        // No InitializeAsync: never start a real named pipe, provider, or game.
        var game=Path.Combine(temporary.Path,"mock-game"); Directory.CreateDirectory(game);
        runtime.Settings.GamePath=game;
        await runtime.InstallEffectsAsync(game, "");
        var rename = runtime.Presets.Single(p => p.HandlerId == "change_player_name").Clone();
        rename.Id = "manual-replay-rename"; rename.IsActive = true;
        rename.Triggers = [new TriggerRule { Amount=7, Currency="RUB", Enabled=true }];
        // This fixture exercises the queue/wire contract, not NPC spawning.
        // Live-catalog and fan parameter validation have their separate suites.
        var fan = new ActionDefinition
        {
            Id="manual-replay-fan", HandlerId="hardcoon.adb.annoying_fan", DisplayName="Fan queue fixture", IsActive=true,
            Triggers=[new TriggerRule { Amount=7, Currency="RUB", Enabled=true }]
        };
        runtime.Settings.Actions = [rename, fan];
        runtime.Settings.Armed = false;
        var donation = new DonationEvent
        {
            Id="received-while-off", Username="Исходный донатер", Message="Исходный текст доната",
            Amount=7, Currency="RUB", CreatedAt=DateTimeOffset.UtcNow, ReceivedAt=DateTimeOffset.UtcNow
        };
        await runtime.ProcessDonationAsync(donation);
        var historyStore = new JsonLineHistoryStore(paths);
        var skipped = (await historyStore.LoadRecentAsync(100)).Single();
        Check(skipped.State==DispatchState.Skipped && skipped.ActionId.Length==0 && skipped.ActionSnapshot is null,
            "Disabled reception unexpectedly selected/executed an action.");
        Check(skipped.Donor==donation.Username && skipped.DonationMessage==donation.Message && skipped.CanRetryManually,
            "Disabled reception lost donor/message or retry eligibility.");
        var vm = new MainViewModel(runtime) { SelectedHistory=skipped };
        Check(vm.RetryCommand.CanExecute(null), "Disabled-reception history still has a disabled repeat button.");
        vm.SelectedHistory = new EventHistoryEntry();
        Check(!vm.RetryCommand.CanExecute(null), "Empty history row became repeatable.");
        await runtime.RetryManuallyAsync(skipped);
        var queueStore = new JsonPendingDispatchStore(paths);
        var pending = await queueStore.LoadAsync();
        Check(pending.Count==2 && pending.All(p => !p.InFlight && p.Donation.Id.StartsWith("manual:")),
            "Button repeat must queue ALL matching actions, not send while game unavailable.");
        Check(pending.All(p => p.Donation.Username==donation.Username && p.Donation.Message==donation.Message) &&
            pending.Single(p=>p.Action.HandlerId==fan.HandlerId).Command.Parameters["donation_message"]==donation.Message &&
            pending.All(p=>p.Command.Parameters["donor_name"]==donation.Username), "Button replay lost original donor/message.");
        Check(!runtime.Settings.Armed, "Manual repeat enabled automatic reception.");
        Check(await runtime.RepeatLastDonationAsync()==DispatchState.Queued, "Hotkey repeat was blocked by disabled reception.");
        pending = await queueStore.LoadAsync();
        Check(pending.Count==4 && pending.Count(p=>p.Donation.Id.StartsWith("hotkey:"))==2,
            "Hotkey did not repeat the original real donation/current rule group.");
        var legacy = new EventHistoryEntry
        {
            DonationId="old-disabled-reception", State=DispatchState.Skipped, CanRetryManually=true,
            Donor=donation.Username, Amount=7, Currency="RUB"
        };
        Check(BridgeRuntime.CanRetryHistoryEntry(legacy), "Old disabled-reception entries remain unrepeatable.");
        await runtime.RetryManuallyAsync(legacy);
        Check((await queueStore.LoadAsync()).Count==6, "Legacy button repeat did not resolve current rules.");
        await runtime.RetryManuallyAsync(new EventHistoryEntry
        {
            DonationId="off-with-no-rule", State=DispatchState.Skipped, CanRetryManually=true,
            Amount=999, Currency="USD"
        });
        Check((await queueStore.LoadAsync()).Count==6 &&
            (await historyStore.LoadRecentAsync(100)).Any(e=>e.State==DispatchState.NoRule),
            "No-match repeat chose an arbitrary action instead of recording NoRule.");
        await Task.WhenAll(runtime.RetryManuallyAsync(skipped), runtime.RetryManuallyAsync(skipped));
        Check((await queueStore.LoadAsync()).Count==10, "Concurrent manual replays were lost or duplicated.");
        Check((await new JsonLineProcessedDonationStore(paths).FindAsync(donation.Id))?.State==DispatchState.Skipped &&
            !runtime.Settings.Armed, "Replay mutated the original donation or enabled reception.");

        const string donor="Исправленный донатер";
        const string message="Исправленный текст\nдоната";
        await runtime.RunAsDonorAsync(rename,"  "+donor+"  ",message);
        try{await runtime.RunAsDonorAsync(fan,donor,message);throw new InvalidOperationException("A catalog-dependent manual launch accepted an offline catalog.");}
        catch(InvalidOperationException error) when(error.Message.Contains("свежий каталог",StringComparison.Ordinal)){}
        Check((await queueStore.LoadAsync()).Count==11,"A rejected offline template entered the queue.");
        var catalog=new CatalogSnapshot(new CatalogRevision("session:manual-donor"),new string('a',64),[]);
        typeof(BridgeRuntime).GetField("_catalogState",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .SetValue(runtime,new CatalogRuntimeState(CapabilitySet.Empty,catalog,true,"Isolated live-catalog fixture"));
        fan.CatalogRevision=catalog.Revision;
        await runtime.RunAsDonorAsync(fan,donor,message);
        pending=await queueStore.LoadAsync();
        var donorRuns=pending.Where(item=>item.Donation.Id.StartsWith("manual:donor:",StringComparison.Ordinal)).ToArray();
        Check(pending.Count==12&&donorRuns.Length==2&&donorRuns.All(item=>item.Donation.Username==donor&&item.Donation.Message==message),
            "Manual donor launch selected extra rules or lost the supplied name/message.");
        Check(donorRuns.All(item=>item.Command.Parameters["donor_name"]==donor)&&donorRuns.Single(item=>item.Action.HandlerId==fan.HandlerId).Command.Parameters["donation_message"]=="Исправленный текст доната",
            "Manual donor launch did not pass donor context to the game command.");
        var donorHistory=(await historyStore.LoadRecentAsync(100)).Where(item=>item.DonationId.StartsWith("manual:donor:",StringComparison.Ordinal)).ToArray();
        Check(donorHistory.Length==2&&donorHistory.All(item=>item.Donor==donor&&item.DonationMessage==message&&item.ActionSnapshot is not null&&item.State==DispatchState.Queued),
            "Manual donor launch lost its repeatable snapshot or donor context in history.");
        try{await runtime.RunAsDonorAsync(rename," \t ",message);throw new InvalidOperationException("Blank donor was accepted.");}
        catch(ArgumentException){}
        Check((await queueStore.LoadAsync()).Count==12,"Invalid donor queued an effect.");
        var prompted=false;
        var cancelled=new MainViewModel(runtime){PromptForDonorLaunch=_=>{prompted=true;return null;}};
        var actionView=new ActionViewModel(rename);
        Check(cancelled.RunAsDonorCommand.CanExecute(actionView),"Manual donor launch is disabled for a supported template.");
        cancelled.RunAsDonorCommand.Execute(actionView);
        Check(prompted&&(await queueStore.LoadAsync()).Count==12,"Cancelling donor input queued an effect.");
        Check(await runtime.RepeatLastDonationAsync()==DispatchState.Queued,"A manual donor launch replaced the last real donation.");
        pending=await queueStore.LoadAsync();
        Check(pending.Count==14&&pending.Where(item=>item.Donation.Id.StartsWith("hotkey:",StringComparison.Ordinal)).All(item=>item.Donation.Username==donation.Username)&&!runtime.Settings.Armed,
            "Manual donor launch polluted real-donation repeat or enabled reception.");
    }
}

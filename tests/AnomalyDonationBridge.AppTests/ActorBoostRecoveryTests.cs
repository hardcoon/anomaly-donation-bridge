using System.IO;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

internal static class ActorBoostRecoveryTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        using var temporary = new Program.TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        await using var runtime = new BridgeRuntime(paths); // Never start real IPC/accounts.
        runtime.Settings.Armed = false;
        runtime.Settings.GlobalCooldownSeconds = 500;
        var model = new ActionDefinition
        {
            Id = "reset-actor-boost", HandlerId = "actor_boost", DisplayName = "Временно изменить параметры героя",
            ParameterDefinitions = [new() { Key = "duration_seconds", Kind = ParameterKind.Integer, Minimum = 5, Maximum = 3600, DefaultValue = "60" }],
            Parameters = new() { ["duration_seconds"] = "60" }
        };
        var source = new ActionViewModel(model);
        var editor = runtimeEditor();
        ActionEditorViewModel runtimeEditor() => new MainViewModel(runtime).CreateActionEditor(source);
        Check(editor.ShowActorBoostReset && editor.CanResetActorBoost, "The actor-boost editor has no recovery action.");
        Check(!await editor.ResetActorBoostAsync() && editor.Error.Contains("Загрузите сохранение"),
            "Disconnected recovery hid the error or tried to queue a game effect.");
        Check(!File.Exists(paths.PendingQueueFile) && !runtime.Settings.Armed,
            "Offline reset changed donation processing or created a queued action.");
        var sent = new List<GameCommand>();
        var acknowledgement = new TaskCompletionSource<GameExecutionResult>();
        runtime.ActorBoostResetTransportForTests = (command, _) =>
        { sent.Add(command); return acknowledgement.Task; };
        // Recovery is independent of uncommitted invalid template input.
        editor.Draft.Parameters.Single().Value = "";
        var reset = editor.ResetActorBoostAsync();
        Check(editor.IsBusy && editor.IsResetting && !editor.CanResetActorBoost && sent.Count == 1,
            "Reset did not wait for the game's acknowledgement or allowed duplicate clicks.");
        Check(!await editor.SaveAsync() && !await editor.ResetActorBoostAsync(), "Saving or another reset raced a pending recovery.");
        var command = sent.Single();
        Check(ActorBoostRecovery.IsResetCommand(command) && command.Parameters.Count == 1 &&
            !command.SaveBeforeBatch && !command.SaveAfterBatch && !command.ShowGameEffectLog,
            "The reset sent template draft values or ordinary effect/save flags.");
        Check(!File.Exists(paths.PendingQueueFile) && runtime.Settings.GlobalCooldownSeconds == 500 && !runtime.Settings.Armed,
            "Explicit recovery used the normal donation queue, cooldown or arming switch.");
        acknowledgement.SetResult(new() { CommandId = command.CommandId, Status = GameResultStatus.Executed, Reason = "actor_boost_reset_defaults" });
        Check(await reset && editor.ResetStatus.Contains("Сохраните игру") && !editor.IsBusy,
            "The successful result was not shown or left the editor locked.");
        Check(source.Parameters.Single().Value == "60" && editor.Draft.Parameters.Single().Value == "",
            "Recovery saved or replaced the unsaved template configuration.");

        var safe = new SafeZoneSettings { Enabled = true };
        command.RequireSafeZoneCheck = true; command.SafeZoneRevision = "old";
        Check(SafeZoneConfig.Prepare(command, CapabilitySet.Empty, safe, false).IsValid && !command.RequireSafeZoneCheck && command.SafeZoneRevision == "",
            "A strict manual recovery was blocked by Safe Zone configuration synchronization.");
        command.Parameters["speed_percent"] = "999";
        Check(!ActorBoostRecovery.IsResetCommand(command) && !SafeZoneConfig.Prepare(command, CapabilitySet.Empty, safe, false).IsValid,
            "An ordinary or mixed command gained the recovery policy bypass.");
        var other = new ActionEditorViewModel(new(new ActionDefinition { HandlerId = "god_mode", DisplayName = "Другой шаблон" }), _ => Task.CompletedTask,
            () => Task.FromResult("unexpected"));
        Check(!other.ShowActorBoostReset && !other.CanResetActorBoost && !await other.ResetActorBoostAsync(),
            "The reset button leaked into other templates.");

        runtime.ActorBoostResetTransportForTests = (cmd, _) => Task.FromResult(new GameExecutionResult
        { CommandId = cmd.CommandId, Status = GameResultStatus.Rejected, Reason = "actor_boost_reset_defaults_unavailable" });
        Check(!await editor.ResetActorBoostAsync() && editor.Error.Contains("конфигурации") && editor.ResetStatus == "" && !editor.IsBusy,
            "Missing native defaults were displayed as a successful reset.");
        Check(!File.Exists(paths.PendingQueueFile), "A failed recovery was queued for surprise execution later.");
    }
}

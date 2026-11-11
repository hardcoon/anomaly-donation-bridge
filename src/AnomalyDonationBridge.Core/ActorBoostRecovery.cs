namespace AnomalyDonationBridge.Core;

public static class ActorBoostRecovery
{
    public const string HandlerId = "actor_boost";
    public const string Parameter = "reset_to_defaults";
    public const string Capability = "actor_boost_reset_v1";

    public static GameCommand CreateCommand() => new()
    {
        DonationId = "manual:actor-boost-reset:" + Guid.NewGuid().ToString("N"),
        ActionId = HandlerId,
        ActionName = "Сброс параметров героя к базовым",
        Parameters = new(StringComparer.OrdinalIgnoreCase) { [Parameter] = "true" },
        SaveBeforeBatch = false, SaveAfterBatch = false,
        ShowGameEffectLog = false, ShowPdaEffectMessage = false
    };

    public static bool IsResetCommand(GameCommand command) =>
        command.ActionId == HandlerId && command.Parameters is { Count: 1 } &&
        command.Parameters.TryGetValue(Parameter, out var value) && value == "true";
}

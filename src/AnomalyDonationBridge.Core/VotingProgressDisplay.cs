using System.Globalization;

namespace AnomalyDonationBridge.Core;

// Shared by the game HUD and OBS; changing display format must not affect the
// round's conditions, currency accounting or the roulette probabilities.
public static class VotingProgressDisplay
{
    public static decimal Percent(VotingRound round, DateTimeOffset now) => round.Phase is VotingPhase.Ready or VotingPhase.Drawn ? 100 :
        round.MoneyGoal ? Math.Clamp(round.Total / Math.Max(.000001m, round.Target) * 100, 0, 100) :
        round.Deadline is { } deadline ? Math.Clamp(100m - (decimal)(deadline - (round.PausedAt ?? now)).TotalSeconds /
            Math.Max(1, round.DurationSeconds) * 100, 0, 100) : 0;

    public static string Value(VotingRound round, VotingHudFormat format, DateTimeOffset now) => format switch
    {
        VotingHudFormat.Amount => Amount(round.Total, round.Currency) + (round.MoneyGoal ? " / " + Amount(round.Target, round.Currency) : ""),
        VotingHudFormat.Timer => round.TimeGoal ? Timer(round, now) : "Таймер выключен",
        _ => Number(Percent(round, now)) + "%"
    };

    public static string Remaining(VotingRound round, DateTimeOffset now) =>
        round.TimeGoal && round.Phase is VotingPhase.Draft or VotingPhase.Collecting or VotingPhase.Paused ? "Осталось " + Timer(round, now) : "";

    public static string Timer(VotingRound round, DateTimeOffset now)
    {
        var at = round.ClosedAt ?? round.PausedAt ?? now;
        var seconds = round.Phase is VotingPhase.Ready or VotingPhase.Failed or VotingPhase.Drawn ? 0 :
            round.Deadline is { } end ? Math.Max(0, (int)Math.Ceiling((end - at).TotalSeconds)) : round.DurationSeconds;
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public static string OptionValue(VotingRound round, VotingOption option, VotingHudFormat format) => format == VotingHudFormat.Amount
        ? Amount(option.Amount, round.Currency) : Number(round.Total > 0 ? option.Amount / round.Total * 100 : 0) + "%";
    private static string Number(decimal value) => value.ToString("0.#", CultureInfo.GetCultureInfo("ru-RU"));
    private static string Amount(decimal value, string currency) => value.ToString("#,0.########", CultureInfo.GetCultureInfo("ru-RU")) +
        " " + (currency == "RUB" ? "руб." : currency);
}

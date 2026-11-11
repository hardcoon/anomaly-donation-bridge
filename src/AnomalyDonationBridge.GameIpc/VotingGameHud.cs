using System.Globalization;
using System.Text;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.GameIpc;

public static class VotingGameHud
{
    public const string Capability = "voting_hud_v1";
    // Empty rows separate command/name pairs; mirror these heights in the addon.
    public static int RowHeight(string line) => line.Length == 0 ? 8 : 20;
    public static string Frame(VotingRound? source, VotingAutomation settings, DateTimeOffset now)
    {
        if (!settings.ShowGameHud || source is not { IsActive: true } || source.Phase is VotingPhase.Draft or VotingPhase.Paused ||
            source.Phase == VotingPhase.Drawn && (source.DeliveryAccepted || source.DeliveryCancelled))
            return VotingGameWire.Line("VOTING_HUD", new string('0', 32), "hidden", "");
        return VotingGameWire.Line("VOTING_HUD", VotingGameWire.Id(source.Id),
            "xy," + settings.GameHudX.ToString(CultureInfo.InvariantCulture) + "," + settings.GameHudY.ToString(CultureInfo.InvariantCulture),
            string.Join('\n', ContentLines(source, settings, now)));
    }

    public static IReadOnlyList<string> ContentLines(VotingRound source, VotingAutomation settings, DateTimeOffset now)
    {
        var round = source.ResultSnapshot();
        var percent = VotingProgressDisplay.Percent(round, now);
        var filled = Math.Clamp((int)(percent / 5), 0, 20);
        var value = VotingProgressDisplay.Value(round, settings.GameHudFormat, now);
        var lines = new List<string> { round.Title, "[" + new string('|', filled) + new string('.', 20 - filled) + "]" +
            (settings.GameHudFormat == VotingHudFormat.Percent ? "  " + value : "") };
        if (settings.GameHudFormat != VotingHudFormat.Percent) lines.Add(value);
        if (settings.GameHudFormat != VotingHudFormat.Timer && VotingProgressDisplay.Remaining(round, now) is { Length: > 0 } remaining)
        {
            lines.Add(remaining);
        }
        if (round.Phase is VotingPhase.Ready or VotingPhase.Failed or VotingPhase.Drawn) lines.Add("Голосование завершено");
        if (settings.ShowGameHudCommands)
        {
            var options = round.Options.Where(option => option.Enabled && option.Preset is not null).ToList();
            foreach (var option in options.Take(6))
            {
                if (settings.ShowGameHudTemplateNames && option != options[0]) lines.Add(string.Empty);
                lines.Add(option.Command + "  " + VotingProgressDisplay.OptionValue(round, option, settings.GameHudFormat));
                if (settings.ShowGameHudTemplateNames)
                {
                    var name = option.Preset!.DisplayName;
                    lines.Add(string.IsNullOrWhiteSpace(name) ? "Без названия" : name);
                }
            }
            if (options.Count > 6) lines.Add("Ещё команд: " + (options.Count - 6));
        }
        return lines.Select(Clip).ToArray();
    }
    private static string Clip(string value)
    {
        if (value.Length == 0) return string.Empty;
        var result = new StringBuilder(); var bytes = 0; var count = 0;
        foreach (var rune in VotingGameWire.Text(value).EnumerateRunes())
        { if (bytes + rune.Utf8SequenceLength > 117 || count >= 57) { result.Append("..."); break; } result.Append(rune); bytes += rune.Utf8SequenceLength; count++; }
        return result.ToString();
    }
}

public sealed partial class NamedPipeGameServer
{
    public async Task SendVotingHudAsync(string line, CancellationToken token = default)
    {
        if (!line.StartsWith("VOTING_HUD\t", StringComparison.Ordinal) || line.Contains('\n') || line.Contains('\r') ||
            Encoding.UTF8.GetByteCount(line) + 1 > PipeProtocol.MaximumLineBytes)
            throw new InvalidDataException("Некорректная строка игрового прогресса.");
        await _votingExchangeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            System.IO.Pipes.NamedPipeServerStream? pipe;
            lock (_stateGate) pipe = _activePipe is { IsConnected: true } connected ? connected : null;
            if (pipe is null || !Capabilities.SupportsCapability(VotingGameHud.Capability))
                throw new InvalidOperationException("Игровой прогресс недоступен в подключённом аддоне.");
            await WriteLineAsync(pipe, line, token).ConfigureAwait(false);
        }
        finally { _votingExchangeGate.Release(); }
    }
}

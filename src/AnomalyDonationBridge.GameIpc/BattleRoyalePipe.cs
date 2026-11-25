using System.Globalization;
using System.IO.Pipes;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.GameIpc;

public sealed record BattleRoyaleStatus(string MatchId, string OpeningDonationId, long OpenedAtMilliseconds,
    int Slots, bool Recruiting, bool HoldEffects, IReadOnlySet<string> Participants) : InboundMessage
{
    public static BattleRoyaleStatus Idle { get; } = new("", "", 0, 0, false, false, new HashSet<string>());

    internal bool Blocks(string actionId) => MatchId.Length != 0 &&
        (actionId == BattleRoyalePipe.Handler || HoldEffects ||
         !Recruiting && actionId is "teleport_random_level" or "hardcoon.adb.new_life" or "hardcoon.adb.new_life_azazel");
}

public sealed record BattleRoyaleParticipant(string Key, string Name);

public static class BattleRoyalePipe
{
    public const string Capability = "battle_royale_v1";
    public const string Handler = "hardcoon.adb.battle_royale";
    public const string Query = "BATTLE_STATUS_QUERY";
    public const string OpenedParameter = "adb_battle_opened_ms";
    public const int ParticipantsPerMessage = 7;

    public static string Cancel(string matchId) => Encode("BATTLE_CANCEL", matchId);

    public static string Roster(string matchId, IEnumerable<BattleRoyaleParticipant> participants) =>
        Encode(["BATTLE_ROSTER", matchId, .. participants.SelectMany(entry => new[] { entry.Key, entry.Name })]);

    private static string Encode(params string[] fields) => string.Join('\t', fields.Select(PipeProtocol.PercentEncode));

    internal static BattleRoyaleStatus Parse(string[] fields)
    {
        if (fields.Length == 2 && fields[1] == "idle") return BattleRoyaleStatus.Idle;
        if (fields.Length != 8) throw Invalid();
        var data = fields.Skip(1).Select(PipeProtocol.PercentDecode).ToArray();
        if (data[0] is not ("recruiting" or "preparing" or "fighting") ||
            !Guid.TryParseExact(data[1], "N", out _) || data[2].Length is < 1 or > 256 ||
            data[2].Any(char.IsControl) ||
            !long.TryParse(data[3], NumberStyles.None, CultureInfo.InvariantCulture, out var opened) ||
            opened is < 1 or > 253402300799999L ||
            !int.TryParse(data[4], NumberStyles.None, CultureInfo.InvariantCulture, out var slots) ||
            slots is < 1 or > 32 || data[5] is not ("0" or "1")) throw Invalid();

        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (data[6].Length != 0)
        {
            foreach (var key in data[6].Split(','))
                if (key.Length != 64 || !key.All(char.IsAsciiHexDigitLower) || !keys.Add(key)) throw Invalid();
        }
        if (keys.Count > slots) throw Invalid();
        return new(data[1], data[2], opened, slots, data[0] == "recruiting", data[5] == "1", keys);
    }

    private static InvalidDataException Invalid() => new("Некорректные данные «Батлл-рояля» от игры.");
}

public sealed partial class NamedPipeGameServer
{
    private BattleRoyaleStatus? _battle;
    public BattleRoyaleStatus? BattleStatus { get { lock (_stateGate) return _battle; } }

    public async Task<bool> SendBattleUpdateAsync(string line, CancellationToken token)
    {
        NamedPipeServerStream? pipe;
        lock (_stateGate)
        {
            pipe = _capabilities.SupportsCapability(BattleRoyalePipe.Capability) && _activePipe is { IsConnected: true }
                ? _activePipe : null;
        }
        if (pipe is null) return false;
        await WriteLineAsync(pipe, line, token).ConfigureAwait(false);
        return true;
    }
}

using System.Globalization;
using System.IO.Pipes;
using System.Text;

namespace AnomalyDonationBridge.GameIpc;

public sealed record TraitorRecruitmentStatus(string RoundId, string OpeningDonationId,
    long OpenedAtMilliseconds, int Needed, string Cursor) : InboundMessage
{
    public static TraitorRecruitmentStatus Idle { get; } = new("", "", 0, 0, "");
}

public sealed record RecruitmentDonation(string DonationId, string Name);

public static class TraitorPipe
{
    public const string Handler = "hardcoon.adb.traitor";
    public const string Capability = "traitor_recruitment_v1";
    public const string Query = "TRAITOR_STATUS_QUERY";
    public const string OpenedParameter = "adb_traitor_opened_ms";
    private const int RosterHeaderFields = 4;
    public const int ParticipantsPerMessage = (PipeProtocol.MaximumFields - RosterHeaderFields) / 2;

    public static string Roster(TraitorRecruitmentStatus status, IEnumerable<RecruitmentDonation> donations, bool last)
    {
        var fields = new List<string> { "TRAITOR_ROSTER", status.RoundId, status.Cursor, last ? "last" : "more" };
        foreach (var donation in donations)
        {
            if (fields.Count + 2 > PipeProtocol.MaximumFields)
                throw new ArgumentOutOfRangeException(nameof(donations), "The traitor roster exceeds the addon's field limit.");
            fields.Add(donation.DonationId);
            fields.Add(donation.Name);
        }
        return string.Join('\t', fields.Select(PipeProtocol.PercentEncode));
    }

    public static bool ValidDonationId(string id) =>
        id.Length > 0 && Encoding.UTF8.GetByteCount(id) <= 256 && !id.Any(char.IsControl);

    internal static TraitorRecruitmentStatus Parse(string[] fields)
    {
        if (fields.Length == 2 && fields[1] == "idle") return TraitorRecruitmentStatus.Idle;
        if (fields.Length == 7 && fields[1] == "recruiting")
        {
            var round = PipeProtocol.PercentDecode(fields[2]);
            var opening = PipeProtocol.PercentDecode(fields[3]);
            var cursor = PipeProtocol.PercentDecode(fields[6]);
            if (Guid.TryParseExact(round, "N", out _) && ValidDonationId(opening) &&
                (cursor.Length == 0 || ValidDonationId(cursor)) &&
                long.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var opened) &&
                opened is > 0 and <= 253402300799999L &&
                int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var needed) && needed > 0)
                return new(round, opening, opened, needed, cursor);
        }
        throw new InvalidDataException("Некорректные данные набора для «Предателя в отряде».");
    }
}

public sealed partial class NamedPipeGameServer
{
    private TraitorRecruitmentStatus? _traitorRecruitment;
    public TraitorRecruitmentStatus? TraitorRecruitment { get { lock (_stateGate) return _traitorRecruitment; } }

    public async Task<bool> SendTraitorUpdateAsync(string line, CancellationToken token)
    {
        NamedPipeServerStream? pipe;
        lock (_stateGate)
        {
            pipe = _capabilities.SupportsCapability(TraitorPipe.Capability) && _activePipe is { IsConnected: true }
                ? _activePipe : null;
        }
        if (pipe is null) return false;
        await WriteLineAsync(pipe, line, token).ConfigureAwait(false);
        return true;
    }
}

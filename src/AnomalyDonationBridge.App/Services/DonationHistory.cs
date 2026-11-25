using AnomalyDonationBridge.Core;
using AnomalyDonationBridge.GameIpc;
using AnomalyDonationBridge.Persistence;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace AnomalyDonationBridge.App.Services;

internal static class DonationHistory
{
    // DonationAlerts keeps its raw id. DonateX uses a provider prefix.
    // Tests, repeats, hotkeys and synthetic voting results use other prefixes.
    public static bool IsPaid(EventHistoryEntry entry) =>
        entry.Amount > 0 && entry.State != DispatchState.Duplicate &&
        !string.IsNullOrWhiteSpace(entry.DonationId) &&
        (!entry.DonationId.Contains(':') || entry.DonationId.StartsWith("donatex:", StringComparison.Ordinal));

    public static async Task<EventHistoryEntry?> FindReplayTargetAsync(JsonLineHistoryStore history, DonationEvent trigger,
        int offset, CancellationToken token)
    {
        var entries = await EventHistoryProjection.LatestPerCommandAsync(
            RecordedEffectsBefore(history, trigger, token), offset, token).ConfigureAwait(false);
        return offset > 0 && entries.Count == offset ? entries[^1] : null;
    }

    private static async IAsyncEnumerable<EventHistoryEntry> RecordedEffectsBefore(
        JsonLineHistoryStore history, DonationEvent trigger, [EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var entry in history.ReadAsync(token).ConfigureAwait(false))
        {
            var handler = entry.ActionSnapshot?.HandlerId ?? entry.HandlerId;
            if (entry.DonationId != trigger.Id &&
                (entry.DonationReceivedAt ?? entry.Timestamp) <= trigger.ReceivedAt &&
                (!string.IsNullOrWhiteSpace(handler) || !string.IsNullOrWhiteSpace(entry.ActionId)) &&
                !DonationDispatcher.IsBridgeMetaHandler(handler))
            {
                yield return entry;
            }
        }
    }

    public static bool WasReceivedDuringRecruitment(EventHistoryEntry entry, BattleRoyaleStatus recruitment) =>
        WasReceivedDuringRecruitment(entry, recruitment.OpeningDonationId, recruitment.OpenedAtMilliseconds,
            recruitment.Participants.Count == 0);

    private static bool WasReceivedDuringRecruitment(EventHistoryEntry entry, string opening, long opened, bool includeOpening) =>
        IsPaid(entry) && (entry.DonationId == opening
            ? includeOpening
            : entry.DonationReceivedAt is { } received &&
              received.ToUnixTimeMilliseconds() >= opened &&
              entry.DonationCreatedAt.ToUnixTimeSeconds() >= opened / 1000);

    public static async Task<IReadOnlyList<BattleRoyaleParticipant>> RecruitAsync(
        JsonLineHistoryStore history, BattleRoyaleStatus recruitment, CancellationToken token)
    {
        var remaining = recruitment.Slots - recruitment.Participants.Count;
        if (remaining <= 0) return [];

        var names = recruitment.Participants.ToHashSet(StringComparer.Ordinal);
        var participants = new List<BattleRoyaleParticipant>(remaining);
        await foreach (var donation in RecruitmentDonationsAsync(history, recruitment.OpeningDonationId,
            recruitment.OpenedAtMilliseconds, recruitment.Participants.Count == 0, token).ConfigureAwait(false))
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                donation.Name.Normalize(NormalizationForm.FormKC).ToUpperInvariant()))).ToLowerInvariant();
            if (!names.Add(key)) continue;
            participants.Add(new(key, donation.Name));
            if (participants.Count == remaining) break;
        }
        return participants;
    }

    public static async Task<List<RecruitmentDonation>> TraitorCandidatesAsync(
        JsonLineHistoryStore history, TraitorRecruitmentStatus recruitment, CancellationToken token)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var latest = new Dictionary<string, (RecruitmentDonation Donation, DateTimeOffset ReceivedAt)>(
            StringComparer.OrdinalIgnoreCase);
        await foreach (var entry in history.ReadAsync(token).ConfigureAwait(false))
        {
            if (!IsPaid(entry) || !TraitorPipe.ValidDonationId(entry.DonationId)) continue;
            var received = entry.DonationReceivedAt ?? EventHistoryProjection.SortTime(entry);
            if (received.ToUnixTimeMilliseconds() > recruitment.OpenedAtMilliseconds || !seen.Add(entry.DonationId)) continue;
            var name = RecruitmentName(entry.Donor);
            if (name.Length == 0) continue;
            if (!latest.TryGetValue(name, out var other) || entry.DonationId == recruitment.OpeningDonationId ||
                (other.Donation.DonationId != recruitment.OpeningDonationId && received >= other.ReceivedAt))
                latest[name] = (new(entry.DonationId, name), received);
        }
        return latest.Values
            .OrderByDescending(item => item.Donation.DonationId == recruitment.OpeningDonationId)
            .ThenByDescending(item => item.ReceivedAt)
            .ThenByDescending(item => item.Donation.DonationId, StringComparer.Ordinal)
            .Select(item => item.Donation)
            .ToList();
    }

    private static async IAsyncEnumerable<RecruitmentDonation> RecruitmentDonationsAsync(
        JsonLineHistoryStore history, string opening, long opened, bool includeOpening,
        [EnumeratorCancellation] CancellationToken token)
    {
        var donations = new HashSet<string>(StringComparer.Ordinal);
        // The opening donation is recorded before its command is sent to the game.
        await foreach (var entry in history.ReadAsync(token).ConfigureAwait(false))
        {
            if (!WasReceivedDuringRecruitment(entry, opening, opened, includeOpening) || !donations.Add(entry.DonationId)) continue;
            var name = RecruitmentName(entry.Donor);
            if (name.Length > 0) yield return new(entry.DonationId, name);
        }
    }

    private static string RecruitmentName(string? value) => string.Concat((value ?? "").EnumerateRunes()
        .Where(rune => !Rune.IsControl(rune))
        .Take(32)
        .Select(rune => rune.Value is '%' or '\\' ? "_" : rune.ToString())).Trim();
}

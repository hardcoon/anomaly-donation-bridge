using AnomalyDonationBridge.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AnomalyDonationBridge.GameIpc;

public sealed class CatalogSnapshotAssembler
{
    private CatalogBeginMessage? _begin;
    private readonly List<CatalogEntry> _entries = [];

    public bool IsReceiving => _begin is not null;
    public int ReceivedCount => _entries.Count;
    public int ExpectedCount => _begin?.ExpectedCount ?? 0;

    public void Begin(CatalogBeginMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_begin is not null)
        {
            throw new InvalidDataException("Новый CATALOG_BEGIN получен до завершения текущего снимка.");
        }

        _begin = message;
        _entries.Clear();
    }

    public void Add(CatalogEntryMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var begin = _begin ?? throw new InvalidDataException("CATALOG_ENTRY получен без CATALOG_BEGIN.");
        if (message.Revision != begin.Revision)
        {
            throw new InvalidDataException("CATALOG_ENTRY относится к другой ревизии.");
        }

        if (message.Index != _entries.Count || _entries.Count >= begin.ExpectedCount)
        {
            throw new InvalidDataException("Нарушен порядок или количество CATALOG_ENTRY.");
        }

        _entries.Add(message.Entry);
    }

    public CatalogSnapshot End(CatalogEndMessage message, DateTimeOffset? receivedAt = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var begin = _begin ?? throw new InvalidDataException("CATALOG_END получен без CATALOG_BEGIN.");
        try
        {
            if (message.Revision != begin.Revision || message.ActualCount != begin.ExpectedCount ||
                _entries.Count != begin.ExpectedCount ||
                !string.Equals(message.ContentHash, begin.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(CatalogContentHash.Compute(_entries, begin.MonsterCommunities), begin.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("CATALOG_END не совпадает с объявленным снимком.");
            }

            return new CatalogSnapshot(begin.Revision, begin.ContentHash, _entries, receivedAt, begin.MonsterCommunities);
        }
        finally
        {
            Reset();
        }
    }

    public void Reset()
    {
        _begin = null;
        _entries.Clear();
    }
}

public static class CatalogContentHash
{
    public static string Compute(IEnumerable<CatalogEntry> entries, string monsterCommunities = "")
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var index = 0;
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(CanonicalEntryLine(index, entry));
            hash.AppendData(bytes);
            index++;
        }

        _ = MonsterCommunityCatalog.Parse(monsterCommunities);
        if (monsterCommunities.Length > 0)
            hash.AppendData(Encoding.UTF8.GetBytes("monster_communities\t" + PipeProtocol.PercentEncode(monsterCommunities) + "\n"));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string CanonicalEntryLine(int index, CatalogEntry entry)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(entry);
        entry.Validate();
        return string.Join('\t',
            index.ToString(CultureInfo.InvariantCulture),
            PipeProtocol.PercentEncode(entry.SectionId),
            KindToken(entry.Kind),
            PipeProtocol.PercentEncode(entry.DisplayName),
            PipeProtocol.PercentEncode(entry.Category),
            StrengthToken(entry.Strength),
            PipeProtocol.PercentEncode(entry.Faction),
            entry.IsAvailable ? "1" : "0",
            QuarantineToken(entry.QuarantineReason)) +
            (entry.NativeClass.Length > 0 ? "\t" + PipeProtocol.PercentEncode(entry.NativeClass) : string.Empty) + "\n";
    }

    private static string KindToken(CatalogEntryKind kind) => kind switch
    {
        CatalogEntryKind.Mutant => "mutant",
        CatalogEntryKind.Npc => "npc",
        CatalogEntryKind.StalkerSquad => "stalker_squad",
        CatalogEntryKind.MutantSquad => "mutant_squad",
        CatalogEntryKind.Item => "item",
        CatalogEntryKind.Anomaly => "anomaly",
        CatalogEntryKind.Faction => "faction",
        CatalogEntryKind.Level => "level",
        CatalogEntryKind.Helicopter => "helicopter",
        CatalogEntryKind.ArmedVehicle => "armed_vehicle",
        CatalogEntryKind.NpcVisual => "npc_visual",
        CatalogEntryKind.GuitarMusic => "guitar_music",
        _ => throw new InvalidDataException($"Неизвестный тип каталога: {kind}")
    };

    private static string StrengthToken(CatalogStrength strength) => strength switch
    {
        CatalogStrength.Unclassified => "unclassified",
        CatalogStrength.Lite => "lite",
        CatalogStrength.Normal => "normal",
        CatalogStrength.Strong => "strong",
        _ => throw new InvalidDataException($"Неизвестная сила каталога: {strength}")
    };

    private static string QuarantineToken(QuarantineReason reason) => reason switch
    {
        QuarantineReason.None => "none",
        QuarantineReason.StoryOrUnique => "story_unique",
        QuarantineReason.NonTradeable => "non_tradeable",
        QuarantineReason.Scripted => "scripted",
        QuarantineReason.InvalidStructure => "invalid_structure",
        QuarantineReason.Blacklisted => "blacklisted",
        QuarantineReason.MissingRequiredFields => "missing_required_fields",
        QuarantineReason.UnsupportedClass => "unsupported_class",
        _ => throw new InvalidDataException($"Неизвестная причина карантина: {reason}")
    };
}

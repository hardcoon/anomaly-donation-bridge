using System.Globalization;
using System.Text.RegularExpressions;

namespace AnomalyDonationBridge.Core;

/// <summary>Ordered native species metadata from the game's merged system.ini, not a vanilla species list.</summary>
public static class MonsterCommunityCatalog
{
    public const string Companion = "adb_mc2_companion";
    private static readonly Regex Name = new("^[a-z0-9_]{1,63}$", RegexOptions.CultureInvariant);
    public sealed record Community(string Name, int Team, int ActorRelation, bool HasHumanUsers);

    public static IReadOnlyList<Community> Parse(string value)
    {
        if (value is null) throw new InvalidDataException("Таблица видов мутантов отсутствует.");
        if (value.Length == 0) return [];
        if (value.Length > 8192) throw new InvalidDataException("Слишком большая таблица видов мутантов.");
        var result = new List<Community>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in value.Split(';'))
        {
            var fields = row.Split(',');
            if (fields.Length != 4 || !Name.IsMatch(fields[0]) || fields[0] == Companion ||
                !names.Add(fields[0]) ||
                !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var team) || team is < 0 or > 255 ||
                !int.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var relation) || relation is < -2 or > 1 ||
                fields[1] != team.ToString(CultureInfo.InvariantCulture) || fields[2] != relation.ToString(CultureInfo.InvariantCulture) ||
                fields[3] is not ("0" or "1") || (fields[0] == "actor" && fields[3] != "0"))
                throw new InvalidDataException("Некорректная таблица видов мутантов.");
            result.Add(new Community(fields[0], team, relation, fields[3] == "1"));
        }
        if (result.Count is < 2 or > 128 || !names.Contains("actor"))
            throw new InvalidDataException("Неполная таблица видов мутантов.");
        return result;
    }
}

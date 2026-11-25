using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AnomalyDonationBridge.Core;

public enum VotingTemplateSource { Active, UserPresets }
public enum VotingCommandFormat { Number, Initials }

public sealed class VotingGenerationSettings
{
    public bool Enabled { get; set; }
    public VotingTemplateSource Source { get; set; }
    public int Count { get; set; } = 2;
    public string Prefix { get; set; } = "!";
    public VotingCommandFormat Format { get; set; } = VotingCommandFormat.Initials;
    // Null means every current/future template. An empty set means none.
    public HashSet<string>? ActiveTemplateIds { get; set; }
    public HashSet<string>? UserTemplateIds { get; set; }
    [JsonIgnore] public HashSet<string>? SelectedIds => Source == VotingTemplateSource.Active ? ActiveTemplateIds : UserTemplateIds;
    public VotingGenerationSettings Clone() => new()
    {
        Enabled = Enabled, Source = Source, Count = Count, Prefix = Prefix, Format = Format,
        ActiveTemplateIds = ActiveTemplateIds is null ? null : new(ActiveTemplateIds, StringComparer.Ordinal),
        UserTemplateIds = UserTemplateIds is null ? null : new(UserTemplateIds, StringComparer.Ordinal)
    };
}

public static partial class VotingGeneration
{
    public static void ValidateConfiguration(VotingGenerationSettings settings)
    {
        if (settings is null || !Enum.IsDefined(settings.Source) || !Enum.IsDefined(settings.Format))
            throw new InvalidOperationException("Неизвестный способ формирования голосования.");
        if (settings.Count is < 2 or > 99) throw new InvalidOperationException("Количество команд: от 2 до 99.");
        if (settings.Prefix is null || settings.Prefix.EnumerateRunes().Count() > 1 ||
            settings.Prefix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new InvalidOperationException("Символ перед командой: один символ или пустое поле.");
        foreach (var ids in new[] { settings.ActiveTemplateIds, settings.UserTemplateIds })
            if (ids is not null && (ids.Count > 10000 || ids.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidOperationException("Некорректный список выбранных шаблонов.");
    }

    public static IReadOnlyList<ActionDefinition> Eligible(VotingGenerationSettings settings, IReadOnlyList<ActionDefinition> templates)
    {
        ValidateConfiguration(settings);
        return templates.Where(template => settings.SelectedIds is null || settings.SelectedIds.Contains(template.Id))
            .DistinctBy(template => template.Id, StringComparer.Ordinal).Where(template => template.Validate().Count == 0).ToArray();
    }

    public static void ValidateAvailability(VotingGenerationSettings settings, IReadOnlyList<ActionDefinition> templates)
    {
        var available = Eligible(settings, templates).Count;
        if (available < settings.Count)
            throw new InvalidOperationException($"Для {settings.Count} команд нужно отметить хотя бы {settings.Count} корректных шаблонов. Сейчас доступно: {available}.");
    }

    public static List<VotingOption> Generate(VotingGenerationSettings settings, IReadOnlyList<ActionDefinition> templates,
        Func<long, long>? randomBelow = null)
    {
        var candidates = Eligible(settings, templates).ToArray();
        if (candidates.Length < settings.Count)
            throw new InvalidOperationException($"Для {settings.Count} команд нужно отметить хотя бы {settings.Count} корректных шаблонов. Сейчас доступно: {candidates.Length}.");
        var random = randomBelow ?? VotingRules.RandomBelow;
        for (var i = 0; i < settings.Count; i++)
        {
            var offset = random(candidates.Length - i);
            if (offset < 0 || offset >= candidates.Length - i) throw new InvalidOperationException("Некорректный случайный индекс.");
            var j = i + (int)offset;
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        var selected = candidates.Take(settings.Count).ToArray();
        var commands = Commands(selected.Select(template => template.DisplayName).ToArray(), settings);
        return selected.Select((template, index) => new VotingOption
        {
            Command = commands[index], Preset = VotingPresets.Snapshot(template)
        }).ToList();
    }

    public static IReadOnlyList<string> Commands(IReadOnlyList<string> titles, VotingGenerationSettings settings)
    {
        ValidateConfiguration(settings);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(titles.Count);
        var prefix = settings.Prefix.Normalize(NormalizationForm.FormKC);
        for (var i = 0; i < titles.Count; i++)
        {
            var basis = settings.Format == VotingCommandFormat.Number ? (i + 1).ToString(CultureInfo.InvariantCulture) :
                string.Concat(Words().Matches(titles[i].Normalize(NormalizationForm.FormKC))
                    .Select(match => match.Value.EnumerateRunes().First().ToString())).ToUpperInvariant().Normalize(NormalizationForm.FormKC);
            if (basis.Length == 0) basis = "Ш";
            var suffix = ""; var number = 2;
            string command;
            do
            {
                var limit = 80 - prefix.Length - suffix.Length;
                var clipped = new StringBuilder();
                foreach (var rune in basis.EnumerateRunes())
                { if (clipped.Length + rune.Utf16SequenceLength > limit) break; clipped.Append(rune.ToString()); }
                command = VotingRules.NormalizeCommand(prefix + clipped + suffix);
                suffix = (number++).ToString(CultureInfo.InvariantCulture);
            } while (!used.Add(command));
            result.Add(command);
        }
        return result;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}

public sealed partial class VotingCoordinator
{
    private IReadOnlyList<ActionDefinition> GenerationTemplates(VotingRound round, string? profileId = null) =>
        templateProvider?.Invoke(round.ProfileId.Length > 0 ? round.ProfileId : profileId ?? _document.ActiveProfileId,
            round.Generation.Source) ?? [];

    public void ValidateCollectionStart(VotingRound round, string? profileId = null)
    {
        if (round.Generation.Enabled && round.Phase is not (VotingPhase.Collecting or VotingPhase.Paused))
        {
            VotingRules.ValidateSettings(round);
            VotingGeneration.ValidateAvailability(round.Generation, GenerationTemplates(round, profileId));
        }
        else VotingRules.Validate(round);
    }

    public bool CanStartCollection(VotingRound round)
    {
        try { ValidateCollectionStart(round); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private VotingRound PrepareGeneratedRound(VotingDocument document, VotingRound original, string profileId)
    {
        var options = VotingGeneration.Generate(original.Generation, GenerationTemplates(original, profileId), _random);
        var next = original;
        if (original.Phase != VotingPhase.Draft)
        {
            document.PreviousRounds.Add(original.Clone());
            next = original.Clone(); next.Id = Guid.NewGuid().ToString("N");
            ResetChatLedger(next);
            next.WinnerOptionId = ""; next.WinnerFraction = 0;
            next.DeliveryAccepted = next.DeliveryCancelled = false; next.DeliveryNote = "";
            next.DrawnAt = next.ClosedAt = next.PausedAt = next.Deadline = null;
            next.Intervals.Clear(); next.Phase = VotingPhase.Draft;
            ClearFrozenResult(next);
            if (original == document.Current) document.Current = next;
            else document.OtherCollections[document.OtherCollections.IndexOf(original)] = next;
        }
        next.Options = options;
        return next;
    }
}

namespace AnomalyDonationBridge.Core;

public sealed partial class VotingCoordinator
{
    public IReadOnlyList<VotingRound> CollectionsFor(string profileId)
    {
        var document = Volatile.Read(ref _document);
        return AllCollections(document).Where(round => round.ProfileId == profileId)
            .OrderBy(round => document.CollectionOrder.IndexOf(round.CollectionId))
            .Select(round => round.Clone()).ToList();
    }

    public Task EnterProfileAsync(string profileId, CancellationToken token = default) => MutateAsync(document =>
    {
        if (string.IsNullOrWhiteSpace(profileId)) throw new InvalidOperationException("Не выбран профиль голосования.");
        var rounds = AllCollections(document).ToList();
        var needsBinding = rounds.Any(round => round.ProfileId.Length == 0);
        if (document.ActiveProfileId == profileId && !needsBinding) return false;
        // Old releases bound a vote only at its first launch. Preserve those
        // bindings, assigning unused drafts to the profile that owns the editor.
        var oldOwner = document.ActiveProfileId.Length > 0 ? document.ActiveProfileId : profileId;
        foreach (var round in rounds.Where(round => round.ProfileId.Length == 0)) round.ProfileId = oldOwner;
        foreach (var round in document.PreviousRounds.Where(round => round.ProfileId.Length == 0))
            round.ProfileId = rounds.FirstOrDefault(current => current.CollectionId == round.CollectionId)?.ProfileId ?? oldOwner;
        foreach (var round in rounds.Where(round => round.ProfileId != profileId))
        { round.IsActive = false; Pause(round, _now()); }
        document.Automation.Enabled = rounds.Any(round => round.IsActive);
        document.ActiveProfileId = profileId;
        if (document.Current.ProfileId != profileId && rounds.FirstOrDefault(round => round.ProfileId == profileId) is { } target)
        {
            document.OtherCollections.Remove(target);
            document.OtherCollections.Add(document.Current);
            document.Current = target;
        }
        return true;
    }, token);

    public Task CopyProfileAsync(string sourceProfileId, string targetProfileId, CancellationToken token = default) => MutateAsync(document =>
    {
        if (string.IsNullOrWhiteSpace(targetProfileId) || sourceProfileId == targetProfileId)
            throw new InvalidOperationException("Для копирования нужен новый профиль.");
        if (AllCollections(document).Any(round => round.ProfileId == targetProfileId))
            throw new InvalidOperationException("В целевом профиле уже есть голосования.");
        var sources = AllCollections(document).Where(round => round.ProfileId == sourceProfileId)
            .OrderBy(round => document.CollectionOrder.IndexOf(round.CollectionId)).ToList();
        foreach (var source in sources)
        {
            var copy = source.Clone();
            copy.Id = Guid.NewGuid().ToString("N"); copy.CollectionId = Guid.NewGuid().ToString("N");
            copy.ProfileId = targetProfileId; copy.IsActive = false; copy.Phase = VotingPhase.Draft;
            copy.YouTubeParticipants.Clear(); copy.SeenYouTubeMessageIds.Clear(); copy.Intervals.Clear();
            copy.Deadline = copy.PausedAt = copy.ClosedAt = copy.DrawnAt = null;
            copy.WinnerOptionId = string.Empty; copy.WinnerFraction = 0;
            copy.DeliveryAccepted = copy.DeliveryCancelled = false; copy.DeliveryNote = copy.Notice = string.Empty;
            ClearFrozenResult(copy);
            foreach (var option in copy.Options)
            {
                option.Id = Guid.NewGuid().ToString("N"); option.Amount = 0; option.AmountsByCurrency.Clear();
                if (option.Preset is { } preset) preset.Id = ActionDefinition.NewInstanceId();
            }
            document.OtherCollections.Add(copy); document.CollectionOrder.Add(copy.CollectionId);
        }
        return sources.Count > 0;
    }, token);

    public Task DeleteProfileAsync(string profileId, CancellationToken token = default) => MutateAsync(document =>
    {
        var removed = AllCollections(document).Where(round => round.ProfileId == profileId).ToList();
        if (removed.Count == 0) return false;
        foreach (var round in removed)
        {
            CancelPendingResults(document, round.CollectionId, "Профиль удалён.");
            round.IsActive = false; Pause(round, _now()); CloseInterval(round, _now());
            document.PreviousRounds.Add(round);
            document.CollectionOrder.Remove(round.CollectionId);
        }
        var remaining = AllCollections(document).Where(round => round.ProfileId != profileId).ToList();
        document.HasCollections = remaining.Count > 0;
        document.Current = remaining.FirstOrDefault() ?? new VotingRound();
        document.OtherCollections = remaining.Skip(1).ToList();
        document.Automation.Enabled = remaining.Any(round => round.IsActive);
        return true;
    }, token);
}

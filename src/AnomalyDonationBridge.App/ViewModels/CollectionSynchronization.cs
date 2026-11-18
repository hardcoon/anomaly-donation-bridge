using System.Collections.ObjectModel;

namespace AnomalyDonationBridge.App.ViewModels;

internal static class CollectionSynchronization
{
    // No Reset notification: WPF retains the containers, scroll position and
    // focus of surviving items. Enumerating lazily also permits bounded batches.
    internal static IEnumerable<bool> Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var retained = wanted.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (retained.Contains(target[index])) continue;
            target.RemoveAt(index);
            yield return true;
        }
        var existing = target.ToHashSet();
        for (var index = 0; index < wanted.Count; index++)
        {
            if (index < target.Count && EqualityComparer<T>.Default.Equals(target[index], wanted[index])) continue;
            if (existing.Add(wanted[index])) target.Insert(index, wanted[index]);
            else target.Move(target.IndexOf(wanted[index]), index);
            yield return true;
        }
    }
}

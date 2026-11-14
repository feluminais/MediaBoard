using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace CozyCouch.Desktop.ViewModels;

/// <summary>
/// Brings a collection a list control shows up to date with fewer changes than a replacement. A control keeps the
/// elements of the items that stay, so keyboard focus on a row survives a refresh that did not touch that row.
/// </summary>
internal static class CollectionSync
{
    /// <summary>Makes <paramref name="target"/> hold the same items as <paramref name="wanted"/>, in order, with the fewest removals, inserts and moves.</summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance as IEqualityComparer<T>);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;

            var at = -1;
            for (var j = i + 1; j < target.Count && at < 0; j++)
            {
                if (ReferenceEquals(target[j], wanted[i])) at = j;
            }

            if (at >= 0) target.Move(at, i);
            else target.Insert(i, wanted[i]);
        }
    }
}

internal static class CollectionUpdate
{
    private const int SmallChange = 24;

    /// <summary>
    /// Makes <paramref name="target"/> hold <paramref name="wanted"/>. A few items added or removed (a title the scan
    /// just found) change the list row by row, so the controls of the other rows, and the focus on one of them, stay.
    /// Anything bigger (another sort, a filter typed) is one Reset, which is much cheaper for thousands of items.
    /// </summary>
    public static void Apply<T>(BulkObservableCollection<T> target, IReadOnlyList<T> wanted)
        where T : class
    {
        if (IsSmallChange(target, wanted)) CollectionSync.Sync(target, wanted);
        else target.ReplaceAll(wanted);
    }

    private static bool IsSmallChange<T>(IReadOnlyList<T> current, IReadOnlyList<T> wanted)
        where T : class
    {
        var comparer = ReferenceEqualityComparer.Instance as IEqualityComparer<T>;
        var now = new HashSet<T>(current, comparer);
        var next = new HashSet<T>(wanted, comparer);
        var changes = current.Count(c => !next.Contains(c)) + wanted.Count(w => !now.Contains(w));
        if (changes > SmallChange) return false;

        // The items that stay must keep their order, or syncing would be one move per item.
        return current.Where(next.Contains).SequenceEqual(wanted.Where(now.Contains), comparer);
    }
}

/// <summary>An observable list that can swap all of its items for one Reset, however many there are.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the contents; the list control sees one change, not one per item.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

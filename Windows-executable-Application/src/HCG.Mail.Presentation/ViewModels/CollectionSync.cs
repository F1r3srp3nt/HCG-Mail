using System.Collections.ObjectModel;

namespace HCG.Mail.App.ViewModels;

internal static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="collection"/> equal to <paramref name="target"/> (by reference) with
    /// minimal remove/move/insert operations so list selection and scroll position survive refreshes.
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> collection, IReadOnlyList<T> target) where T : class
    {
        var keep = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            var item = target[i];
            if (i < collection.Count && ReferenceEquals(collection[i], item))
                continue;
            var existing = IndexOf(collection, item, i + 1);
            if (existing >= 0)
                collection.Move(existing, i);
            else
                collection.Insert(i, item);
        }

        while (collection.Count > target.Count)
            collection.RemoveAt(collection.Count - 1);
    }

    private static int IndexOf<T>(ObservableCollection<T> collection, T item, int start) where T : class
    {
        for (var i = start; i < collection.Count; i++)
        {
            if (ReferenceEquals(collection[i], item))
                return i;
        }
        return -1;
    }
}

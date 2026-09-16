using System.Collections.ObjectModel;

namespace AnyHttpProxy.UI;

/// <summary>Wiersze aktualizowane w miejscu - lista nie mruga, a checkbox czy pole pod kursorem nie znika.</summary>
public static class RowSync
{
    public static void Sync<TItem, TRow, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TRow, TKey> rowKey,
        Func<TItem, TRow> create,
        Action<TRow, TItem> update)
        where TKey : notnull
    {
        var wanted = items.ToDictionary(itemKey);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!wanted.ContainsKey(rowKey(rows[i]))) rows.RemoveAt(i);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var key = itemKey(items[i]);
            var existing = rows.FirstOrDefault(r => EqualityComparer<TKey>.Default.Equals(rowKey(r), key));

            if (existing is null)
            {
                rows.Insert(Math.Min(i, rows.Count), create(items[i]));
                continue;
            }

            update(existing, items[i]);
            var at = rows.IndexOf(existing);
            if (at != i && i < rows.Count) rows.Move(at, i);
        }
    }
}

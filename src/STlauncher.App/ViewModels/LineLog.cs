using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The lines of a console: a list that can take a batch and drop its oldest lines as one
/// change each. The game prints hundreds of lines a second while it loads; adding them one
/// at a time, and then removing as many one at a time from the top, told the list on
/// screen about every single one of them.
/// </summary>
public sealed class LineLog : ObservableCollection<string>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    /// <summary>Adds the lines at the end, then drops from the top whatever is over <paramref name="keep"/>.</summary>
    public void Append(IReadOnlyList<string> lines, int keep)
    {
        if (lines.Count == 1)
        {
            Add(lines[0]);
        }
        else if (lines.Count > 1)
        {
            CheckReentrancy();

            var start = Count;
            var added = new List<string>(lines);

            ((List<string>)Items).AddRange(added);
            OnPropertyChanged(CountChanged);
            OnPropertyChanged(IndexerChanged);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)added, start));
        }

        var excess = Count - keep;

        if (excess == 1)
        {
            RemoveAt(0);
        }
        else if (excess > 1)
        {
            CheckReentrancy();

            var items = (List<string>)Items;
            var removed = items.GetRange(0, excess);

            items.RemoveRange(0, excess);
            OnPropertyChanged(CountChanged);
            OnPropertyChanged(IndexerChanged);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, (IList)removed, 0));
        }
    }
}

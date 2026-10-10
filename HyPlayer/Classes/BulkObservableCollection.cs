using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HyPlayer.Classes;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        if (Count != 0)
        {
            foreach (var item in items) Add(item);
            return;
        }

        CheckReentrancy();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

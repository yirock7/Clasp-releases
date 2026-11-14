using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace AppleDeviceHub.App.Design;

/// <summary>
/// A read-only view of a .NET collection that XAML can use as an ItemsSource in the Native AOT build:
/// <c>ItemsSource="{x:Bind design:BindableItems.From(Audio.Outputs), Mode=OneWay}"</c>.
/// Native AOT needs every collection handed to XAML to have a WinRT wrapper generated at build time, and CsWinRT
/// only generates those for classes declared in this project, not for <c>ObservableCollection&lt;T&gt;</c> or arrays
/// from AppleDeviceHub.Core. Without this view such a binding fails with "Value does not fall within the expected
/// range". Change notifications from the source collection are forwarded.
/// </summary>
public sealed partial class BindableItems : IList, INotifyCollectionChanged
{
    // One view per source collection, kept only while the source is alive, so reopening a window does not
    // subscribe again to a long-lived view-model collection.
    private static readonly ConditionalWeakTable<IEnumerable, BindableItems> Views = new();

    private readonly IList _items;

    private BindableItems(IEnumerable source)
    {
        _items = source as IList ?? source.Cast<object>().ToList();
        if (source is INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, e) => CollectionChanged?.Invoke(this, e);
    }

    public static BindableItems? From(IEnumerable? source) =>
        source is null ? null : Views.GetValue(source, static items => new BindableItems(items));

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public object? this[int index] { get => _items[index]; set => throw new NotSupportedException(); }
    public int Count => _items.Count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public bool Contains(object? value) => _items.Contains(value);
    public int IndexOf(object? value) => _items.IndexOf(value);
    public void CopyTo(Array array, int index) => _items.CopyTo(array, index);
    public IEnumerator GetEnumerator() => _items.GetEnumerator();
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

using System.Collections;

namespace Vela.Runtime;

/// <summary>
/// Provides a hash map for generated Vela programs. Lookup, insertion, and removal are O(1) expected.
/// </summary>
/// <typeparam name="TKey">The non-null key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class VelaHashMap<TKey, TValue>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _items;

    /// <summary>Initializes an empty hash map with the runtime default capacity.</summary>
    public VelaHashMap()
        : this(0)
    {
    }

    /// <summary>Initializes an empty hash map with at least <paramref name="capacity"/> entry slots.</summary>
    /// <param name="capacity">The initial entry capacity.</param>
    public VelaHashMap(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new Dictionary<TKey, TValue>(capacity, EqualityComparer<TKey>.Default);
    }

    /// <summary>Gets the number of key-value pairs stored in the map.</summary>
    public int Count => _items.Count;

    /// <summary>Gets or sets the value associated with <paramref name="key"/>.</summary>
    /// <param name="key">The key to read or write.</param>
    /// <returns>The value associated with <paramref name="key"/>.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the key is not present during a read.</exception>
    public TValue this[TKey key]
    {
        get => _items[key];
        set => _items[key] = value;
    }

    /// <summary>Adds or replaces the value associated with <paramref name="key"/>.</summary>
    /// <param name="key">The key to add or update.</param>
    /// <param name="value">The value to associate with the key.</param>
    public void Set(TKey key, TValue value) => _items[key] = value;

    /// <summary>Looks up a value without throwing when <paramref name="key"/> is absent.</summary>
    /// <param name="key">The key to look up.</param>
    /// <returns>An option containing the associated value, or an empty option.</returns>
    public Option<TValue> TryGet(TKey key) => _items.TryGetValue(key, out var value)
        ? Option.Some(value)
        : Option.None<TValue>();

    /// <summary>Determines whether <paramref name="key"/> is present.</summary>
    /// <param name="key">The key to test.</param>
    /// <returns><see langword="true"/> when the key is present; otherwise, <see langword="false"/>.</returns>
    public bool Contains(TKey key) => _items.ContainsKey(key);

    /// <summary>Removes the value associated with <paramref name="key"/>.</summary>
    /// <param name="key">The key to remove.</param>
    /// <returns><see langword="true"/> when an entry was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(TKey key) => _items.Remove(key);

    /// <summary>Ensures capacity for at least <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">The requested minimum capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ = _items.EnsureCapacity(capacity);
    }

    /// <summary>Removes all entries from the map.</summary>
    public void Clear() => _items.Clear();
}

/// <summary>
/// Provides a hash set for generated Vela programs. Add, containment, and removal are O(1) expected.
/// </summary>
/// <typeparam name="T">The non-null element type.</typeparam>
public sealed class VelaHashSet<T>
    : IEnumerable<T>
    where T : notnull
{
    private readonly HashSet<T> _items;

    /// <summary>Initializes an empty hash set with the runtime default capacity.</summary>
    public VelaHashSet()
        : this(0)
    {
    }

    /// <summary>Initializes an empty hash set with at least <paramref name="capacity"/> entry slots.</summary>
    /// <param name="capacity">The initial entry capacity.</param>
    public VelaHashSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new HashSet<T>(capacity, EqualityComparer<T>.Default);
    }

    /// <summary>Gets the number of elements stored in the set.</summary>
    public int Count => _items.Count;

    /// <summary>Adds <paramref name="value"/> to the set.</summary>
    /// <param name="value">The value to add.</param>
    /// <returns><see langword="true"/> when the value was new; otherwise, <see langword="false"/>.</returns>
    public bool Add(T value) => _items.Add(value);

    /// <summary>Determines whether <paramref name="value"/> is present.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is present; otherwise, <see langword="false"/>.</returns>
    public bool Contains(T value) => _items.Contains(value);

    /// <summary>Removes <paramref name="value"/> from the set.</summary>
    /// <param name="value">The value to remove.</param>
    /// <returns><see langword="true"/> when the value was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(T value) => _items.Remove(value);

    /// <summary>Ensures capacity for at least <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">The requested minimum capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ = _items.EnsureCapacity(capacity);
    }

    /// <summary>Removes all values from the set.</summary>
    public void Clear() => _items.Clear();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

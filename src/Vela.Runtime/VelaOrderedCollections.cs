using System.Collections;

namespace Vela.Runtime;

#pragma warning disable CA1711 // PriorityQueue and LinkedList are intentional standard-library API names.

/// <summary>Provides deterministic comparers for ordered Vela collections.</summary>
internal static class VelaOrdering
{
    /// <summary>Returns an ordinal comparer for <see cref="string"/> and the default comparer otherwise.</summary>
    /// <typeparam name="T">The compared type.</typeparam>
    /// <returns>A deterministic comparer for <typeparamref name="T"/>.</returns>
    public static IComparer<T> Comparer<T>() => typeof(T) == typeof(string)
        ? (IComparer<T>)(object)StringComparer.Ordinal
        : System.Collections.Generic.Comparer<T>.Default;
}

/// <summary>
/// Provides a key-ordered map for generated Vela programs, comparable to C++ std::map,
/// Java TreeMap, and Rust BTreeMap. Lookup, insertion, and removal are O(log n).
/// </summary>
/// <typeparam name="TKey">The non-null, ordered key type.</typeparam>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class VelaSortedMap<TKey, TValue>
    where TKey : notnull
{
    private readonly SortedDictionary<TKey, TValue> _items;

    /// <summary>Initializes an empty sorted map ordered by the deterministic key comparer.</summary>
    public VelaSortedMap()
    {
        _items = new SortedDictionary<TKey, TValue>(VelaOrdering.Comparer<TKey>());
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

    /// <summary>Returns the smallest key when the map is not empty.</summary>
    /// <returns>An option containing the smallest key, or an empty option.</returns>
    public Option<TKey> FirstKey()
    {
        foreach (var pair in _items)
        {
            return Option.Some(pair.Key);
        }

        return Option.None<TKey>();
    }

    /// <summary>Returns the largest key when the map is not empty.</summary>
    /// <returns>An option containing the largest key, or an empty option.</returns>
    public Option<TKey> LastKey()
    {
        var found = false;
        TKey last = default!;
        foreach (var pair in _items)
        {
            last = pair.Key;
            found = true;
        }

        return found ? Option.Some(last) : Option.None<TKey>();
    }

    /// <summary>Removes all entries from the map.</summary>
    public void Clear() => _items.Clear();
}

/// <summary>
/// Provides an ordered set for generated Vela programs, comparable to C++ std::set,
/// Java TreeSet, and Rust BTreeSet. Add, containment, and removal are O(log n).
/// Iteration yields values in ascending order.
/// </summary>
/// <typeparam name="T">The non-null, ordered element type.</typeparam>
public sealed class VelaSortedSet<T>
    : IEnumerable<T>
    where T : notnull
{
    private readonly SortedSet<T> _items;

    /// <summary>Initializes an empty sorted set ordered by the deterministic comparer.</summary>
    public VelaSortedSet()
    {
        _items = new SortedSet<T>(VelaOrdering.Comparer<T>());
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

    /// <summary>Returns the smallest value when the set is not empty.</summary>
    /// <returns>An option containing the smallest value, or an empty option.</returns>
    public Option<T> First() => _items.Count == 0 ? Option.None<T>() : Option.Some(_items.Min!);

    /// <summary>Returns the largest value when the set is not empty.</summary>
    /// <returns>An option containing the largest value, or an empty option.</returns>
    public Option<T> Last() => _items.Count == 0 ? Option.None<T>() : Option.Some(_items.Max!);

    /// <summary>Removes all values from the set.</summary>
    public void Clear() => _items.Clear();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

/// <summary>
/// Provides a growable double-ended queue for generated Vela programs, comparable to
/// C++ std::deque, Java ArrayDeque, and Rust VecDeque. Push and pop at both ends are
/// amortized O(1). Iteration runs from the front to the back.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaDeque<T> : IEnumerable<T>
{
    private const int MinimumCapacity = 4;

    private T[] _items;
    private int _head;
    private int _count;

    /// <summary>Initializes an empty deque with the runtime default capacity.</summary>
    public VelaDeque()
        : this(0)
    {
    }

    /// <summary>Initializes an empty deque with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The initial element capacity.</param>
    public VelaDeque(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = capacity == 0 ? [] : new T[capacity];
    }

    /// <summary>Gets the number of elements stored in the deque.</summary>
    public int Count => _count;

    /// <summary>Gets the number of elements that fit before the next capacity expansion.</summary>
    public int Capacity => _items.Length;

    /// <summary>Adds <paramref name="value"/> to the front of the deque.</summary>
    /// <param name="value">The value to add.</param>
    public void PushFront(T value)
    {
        EnsureRoom();
        _head = (_head - 1 + _items.Length) % _items.Length;
        _items[_head] = value;
        _count++;
    }

    /// <summary>Adds <paramref name="value"/> to the back of the deque.</summary>
    /// <param name="value">The value to add.</param>
    public void PushBack(T value)
    {
        EnsureRoom();
        _items[(_head + _count) % _items.Length] = value;
        _count++;
    }

    /// <summary>Removes and returns the front value when the deque is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> PopFront()
    {
        if (_count == 0)
        {
            return Option.None<T>();
        }

        var value = _items[_head];
        _items[_head] = default!;
        _head = (_head + 1) % _items.Length;
        _count--;
        return Option.Some(value);
    }

    /// <summary>Removes and returns the back value when the deque is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> PopBack()
    {
        if (_count == 0)
        {
            return Option.None<T>();
        }

        var tail = (_head + _count - 1) % _items.Length;
        var value = _items[tail];
        _items[tail] = default!;
        _count--;
        return Option.Some(value);
    }

    /// <summary>Returns the front value without removing it when the deque is not empty.</summary>
    /// <returns>An option containing the front value, or an empty option.</returns>
    public Option<T> PeekFront() => _count == 0 ? Option.None<T>() : Option.Some(_items[_head]);

    /// <summary>Returns the back value without removing it when the deque is not empty.</summary>
    /// <returns>An option containing the back value, or an empty option.</returns>
    public Option<T> PeekBack() => _count == 0
        ? Option.None<T>()
        : Option.Some(_items[(_head + _count - 1) % _items.Length]);

    /// <summary>Ensures capacity for at least <paramref name="capacity"/> elements.</summary>
    /// <param name="capacity">The requested minimum capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > _items.Length)
        {
            Resize(capacity);
        }
    }

    /// <summary>Removes every element from the deque.</summary>
    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    /// <summary>Returns an enumerator from the front value to the back value.</summary>
    /// <returns>An enumerator over the current deque contents.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < _count; index++)
        {
            yield return _items[(_head + index) % _items.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void EnsureRoom()
    {
        if (_count == _items.Length)
        {
            Resize(Math.Max(MinimumCapacity, _items.Length * 2));
        }
    }

    private void Resize(int capacity)
    {
        var next = new T[capacity];
        for (var index = 0; index < _count; index++)
        {
            next[index] = _items[(_head + index) % _items.Length];
        }

        _items = next;
        _head = 0;
    }
}

/// <summary>
/// Provides a binary min-heap priority queue for generated Vela programs, comparable to
/// C++ std::priority_queue, Java PriorityQueue, Rust BinaryHeap, and Go container/heap.
/// Push and pop are O(log n); peek is O(1). The smallest value is served first.
/// </summary>
/// <typeparam name="T">The ordered element type.</typeparam>
public sealed class VelaPriorityQueue<T>
{
    private readonly PriorityQueue<T, T> _items;

    /// <summary>Initializes an empty priority queue with the runtime default capacity.</summary>
    public VelaPriorityQueue()
        : this(0)
    {
    }

    /// <summary>Initializes an empty priority queue with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The initial element capacity.</param>
    public VelaPriorityQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new PriorityQueue<T, T>(capacity, VelaOrdering.Comparer<T>());
    }

    /// <summary>Gets the number of elements stored in the queue.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the number of elements that fit before the next capacity expansion.</summary>
    public int Capacity => _items.EnsureCapacity(0);

    /// <summary>Adds <paramref name="value"/> to the queue.</summary>
    /// <param name="value">The value to add.</param>
    public void Push(T value) => _items.Enqueue(value, value);

    /// <summary>Removes and returns the smallest value when the queue is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> Pop() => _items.TryDequeue(out var value, out _)
        ? Option.Some(value)
        : Option.None<T>();

    /// <summary>Returns the smallest value without removing it when the queue is not empty.</summary>
    /// <returns>An option containing the smallest value, or an empty option.</returns>
    public Option<T> Peek() => _items.TryPeek(out var value, out _)
        ? Option.Some(value)
        : Option.None<T>();

    /// <summary>Ensures capacity for at least <paramref name="capacity"/> elements.</summary>
    /// <param name="capacity">The requested minimum capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _ = _items.EnsureCapacity(capacity);
    }

    /// <summary>Removes every element from the queue.</summary>
    public void Clear() => _items.Clear();
}

/// <summary>
/// Provides a doubly linked list for generated Vela programs, comparable to C++ std::list,
/// Java LinkedList, and Rust LinkedList. Push and pop at both ends are O(1); value search
/// and removal are O(n). Iteration runs from the front to the back.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaLinkedList<T> : IEnumerable<T>
{
    private readonly LinkedList<T> _items = new();

    /// <summary>Gets the number of elements stored in the list.</summary>
    public int Count => _items.Count;

    /// <summary>Adds <paramref name="value"/> to the front of the list.</summary>
    /// <param name="value">The value to add.</param>
    public void PushFront(T value) => _items.AddFirst(value);

    /// <summary>Adds <paramref name="value"/> to the back of the list.</summary>
    /// <param name="value">The value to add.</param>
    public void PushBack(T value) => _items.AddLast(value);

    /// <summary>Removes and returns the front value when the list is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> PopFront()
    {
        var first = _items.First;
        if (first is null)
        {
            return Option.None<T>();
        }

        _items.RemoveFirst();
        return Option.Some(first.Value);
    }

    /// <summary>Removes and returns the back value when the list is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> PopBack()
    {
        var last = _items.Last;
        if (last is null)
        {
            return Option.None<T>();
        }

        _items.RemoveLast();
        return Option.Some(last.Value);
    }

    /// <summary>Returns the front value without removing it when the list is not empty.</summary>
    /// <returns>An option containing the front value, or an empty option.</returns>
    public Option<T> PeekFront() => _items.First is { } first ? Option.Some(first.Value) : Option.None<T>();

    /// <summary>Returns the back value without removing it when the list is not empty.</summary>
    /// <returns>An option containing the back value, or an empty option.</returns>
    public Option<T> PeekBack() => _items.Last is { } last ? Option.Some(last.Value) : Option.None<T>();

    /// <summary>Determines whether <paramref name="value"/> is present.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is present; otherwise, <see langword="false"/>.</returns>
    public bool Contains(T value) => _items.Contains(value);

    /// <summary>Removes the first occurrence of <paramref name="value"/> from the list.</summary>
    /// <param name="value">The value to remove.</param>
    /// <returns><see langword="true"/> when a value was removed; otherwise, <see langword="false"/>.</returns>
    public bool Remove(T value) => _items.Remove(value);

    /// <summary>Removes every element from the list.</summary>
    public void Clear() => _items.Clear();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

#pragma warning restore CA1711

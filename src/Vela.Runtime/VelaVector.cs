using System.Collections;

namespace Vela.Runtime;

/// <summary>
/// Provides a contiguous, resizable sequence for generated Vela programs.
/// Indexing is O(1); appending and removing the last element are O(1) amortized.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaVector<T> : IEnumerable<T>
{
    private readonly List<T> _items;

    /// <summary>Initializes an empty vector with the runtime default capacity.</summary>
    public VelaVector()
        : this(0)
    {
    }

    /// <summary>Initializes an empty vector with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The initial element capacity.</param>
    public VelaVector(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new List<T>(capacity);
    }

    /// <summary>Initializes a vector by copying <paramref name="items"/>.</summary>
    /// <param name="items">The source elements to copy.</param>
    public VelaVector(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = new List<T>(items);
    }

    /// <summary>Gets the number of elements currently stored in the vector.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the number of elements that fit before the next capacity expansion.</summary>
    public int Capacity => _items.Capacity;

    /// <summary>Gets or sets an element by its zero-based index.</summary>
    /// <param name="index">The zero-based element index.</param>
    /// <returns>The element at <paramref name="index"/>.</returns>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Appends <paramref name="value"/> to the end of the vector.</summary>
    /// <param name="value">The value to append.</param>
    public void Append(T value) => _items.Add(value);

    /// <summary>Removes and returns the last element when the vector is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> Pop()
    {
        if (_items.Count == 0)
        {
            return Option.None<T>();
        }

        var lastIndex = _items.Count - 1;
        var value = _items[lastIndex];
        _items.RemoveAt(lastIndex);
        return Option.Some(value);
    }

    /// <summary>Ensures that the vector can store at least <paramref name="capacity"/> elements without growing.</summary>
    /// <param name="capacity">The requested minimum capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > _items.Capacity)
        {
            _items.Capacity = capacity;
        }
    }

    /// <summary>Removes every element while retaining the allocated capacity.</summary>
    public void Clear() => _items.Clear();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

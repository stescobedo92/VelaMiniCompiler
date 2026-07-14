using System.Collections;

namespace Vela.Runtime;

/// <summary>Provides fixed-size contiguous storage for generated Vela programs.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaArray<T> : IEnumerable<T>
{
    private readonly T[] _items;

    /// <summary>Creates an array with exactly <paramref name="length"/> elements.</summary>
    public VelaArray(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _items = new T[length];
    }

    /// <summary>Creates an array by copying the supplied items.</summary>
    public VelaArray(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    /// <summary>Gets the fixed array length.</summary>
    public int Length => _items.Length;

    /// <summary>Gets or sets an element using normal runtime bounds checks.</summary>
    public T this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>Gets an element and attaches the originating Vela source location on failure.</summary>
    public T Get(int index, string sourceLocation)
    {
        VelaGuards.RequireIndex(index, _items.Length, sourceLocation);
        return _items[index];
    }

    /// <summary>Sets an element and attaches the originating Vela source location on failure.</summary>
    public void Set(int index, T value, string sourceLocation)
    {
        VelaGuards.RequireIndex(index, _items.Length, sourceLocation);
        _items[index] = value;
    }

    /// <summary>Returns a defensive copy of the stored values.</summary>
    public T[] ToArray() => (T[])_items.Clone();

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Provides source-aware guards emitted by the Vela compiler.</summary>
public static class VelaGuards
{
    /// <summary>Returns a non-null reference or raises a Vela null-reference failure.</summary>
    public static T NotNull<T>(T? value, string sourceLocation)
        where T : class => value ?? throw new VelaNullReferenceException(sourceLocation);

    /// <summary>Verifies that an index identifies an element in a collection of the supplied length.</summary>
    public static void RequireIndex(int index, int length, string sourceLocation)
    {
        if ((uint)index >= (uint)length)
        {
            throw new VelaIndexOutOfRangeException(index, length, sourceLocation);
        }
    }

    /// <summary>Returns an optional value or raises a Vela null-reference failure when it is empty.</summary>
    public static T RequireValue<T>(Option<T> value, string sourceLocation) => value.HasValue
        ? value.Value
        : throw new VelaNullReferenceException(sourceLocation);
}

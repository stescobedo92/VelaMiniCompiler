using System.Collections;
using System.Runtime.CompilerServices;

namespace Vela.Runtime;

/// <summary>Provides fixed-size contiguous storage for generated Vela programs.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaArray<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private readonly BitArray? _initialized;

    /// <summary>Creates an array with exactly <paramref name="length"/> elements.</summary>
    public VelaArray(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _items = new T[length];
        // References can be checked directly. Structs can hide null fields in
        // their default value; Option<T> is the exception because default is None.
        if (typeof(T).IsValueType && RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && !(typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(Option<>)))
        {
            _initialized = new BitArray(length);
        }
    }

    /// <summary>Creates an array by copying the supplied items.</summary>
    public VelaArray(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    /// <summary>Gets the fixed array length.</summary>
    public int Length => _items.Length;

    /// <summary>Gets or sets an element, rejecting uninitialized reference-bearing values.</summary>
    public T this[int index]
    {
        get => ReadItem(index, null);
        set => WriteItem(index, value);
    }

    /// <summary>Gets an element and attaches the originating Vela source location on failure.</summary>
    public T Get(int index, string sourceLocation)
    {
        VelaGuards.RequireIndex(index, _items.Length, sourceLocation);
        return ReadItem(index, sourceLocation);
    }

    /// <summary>Sets an element and attaches the originating Vela source location on failure.</summary>
    public void Set(int index, T value, string sourceLocation)
    {
        VelaGuards.RequireIndex(index, _items.Length, sourceLocation);
        WriteItem(index, value);
    }

    /// <summary>Returns a defensive copy of the stored values.</summary>
    public T[] ToArray()
    {
        for (var index = 0; index < _items.Length; index++)
        {
            _ = ReadItem(index, null);
        }

        return (T[])_items.Clone();
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < _items.Length; index++)
        {
            yield return ReadItem(index, null);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private T ReadItem(int index, string? sourceLocation)
    {
        var value = _items[index];
        // Any is represented by object and may intentionally contain nil; its
        // typed unboxing operation performs the required runtime validation.
        if ((value is null && typeof(T) != typeof(object)) || (_initialized is not null && !_initialized[index]))
        {
            throw new VelaNullReferenceException(sourceLocation);
        }

        return value;
    }

    private void WriteItem(int index, T value)
    {
        _items[index] = value;
        if (_initialized is not null)
        {
            _initialized[index] = true;
        }
    }
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

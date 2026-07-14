using System.Collections;

namespace Vela.Runtime;

#pragma warning disable CA1711 // Queue and Stack are intentional standard-library API names.

/// <summary>Provides a first-in, first-out queue for generated Vela programs.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaQueue<T> : IEnumerable<T>
{
    private readonly Queue<T> _items;

    /// <summary>Initializes an empty queue with the runtime default capacity.</summary>
    public VelaQueue()
        : this(0)
    {
    }

    /// <summary>Initializes an empty queue with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The initial element capacity.</param>
    public VelaQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new Queue<T>(capacity);
    }

    /// <summary>Gets the number of elements stored in the queue.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the number of elements that fit before the next capacity expansion.</summary>
    public int Capacity => _items.EnsureCapacity(0);

    /// <summary>Adds <paramref name="value"/> to the end of the queue.</summary>
    /// <param name="value">The value to enqueue.</param>
    public void Enqueue(T value) => _items.Enqueue(value);

    /// <summary>Removes and returns the oldest value when the queue is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> Dequeue() => _items.TryDequeue(out var value)
        ? Option.Some(value)
        : Option.None<T>();

    /// <summary>Returns the oldest value without removing it when the queue is not empty.</summary>
    /// <returns>An option containing the oldest value, or an empty option.</returns>
    public Option<T> Peek() => _items.TryPeek(out var value)
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

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

/// <summary>Provides a last-in, first-out stack for generated Vela programs.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class VelaStack<T> : IEnumerable<T>
{
    private readonly Stack<T> _items;

    /// <summary>Initializes an empty stack with the runtime default capacity.</summary>
    public VelaStack()
        : this(0)
    {
    }

    /// <summary>Initializes an empty stack with at least <paramref name="capacity"/> slots.</summary>
    /// <param name="capacity">The initial element capacity.</param>
    public VelaStack(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = new Stack<T>(capacity);
    }

    /// <summary>Gets the number of elements stored in the stack.</summary>
    public int Count => _items.Count;

    /// <summary>Adds <paramref name="value"/> to the top of the stack.</summary>
    /// <param name="value">The value to push.</param>
    public void Push(T value) => _items.Push(value);

    /// <summary>Removes and returns the top value when the stack is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> Pop() => _items.TryPop(out var value)
        ? Option.Some(value)
        : Option.None<T>();

    /// <summary>Returns the top value without removing it when the stack is not empty.</summary>
    /// <returns>An option containing the top value, or an empty option.</returns>
    public Option<T> Peek() => _items.TryPeek(out var value)
        ? Option.Some(value)
        : Option.None<T>();

    /// <summary>Removes every value from the stack.</summary>
    public void Clear() => _items.Clear();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_items).GetEnumerator();
}

/// <summary>Provides a fixed-capacity circular buffer with O(1) enqueue and dequeue operations.</summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class RingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _items;
    private int _head;
    private int _count;

    /// <summary>Initializes a circular buffer with a fixed positive capacity.</summary>
    /// <param name="capacity">The maximum number of elements.</param>
    public RingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Ring buffer capacity must be positive.");
        }

        _items = new T[capacity];
    }

    /// <summary>Gets the number of elements currently stored in the buffer.</summary>
    public int Count => _count;

    /// <summary>Gets the maximum number of elements the buffer can store.</summary>
    public int Capacity => _items.Length;

    /// <summary>Attempts to add <paramref name="value"/> to the tail of the buffer.</summary>
    /// <param name="value">The value to enqueue.</param>
    /// <returns><see langword="false"/> when the buffer is full; otherwise, <see langword="true"/>.</returns>
    public bool TryEnqueue(T value)
    {
        if (_count == _items.Length)
        {
            return false;
        }

        var tail = (_head + _count) % _items.Length;
        _items[tail] = value;
        _count++;
        return true;
    }

    /// <summary>Removes and returns the oldest value when the buffer is not empty.</summary>
    /// <returns>An option containing the removed value, or an empty option.</returns>
    public Option<T> Dequeue()
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

    /// <summary>Returns the oldest value without removing it when the buffer is not empty.</summary>
    /// <returns>An option containing the oldest value, or an empty option.</returns>
    public Option<T> Peek() => _count == 0 ? Option.None<T>() : Option.Some(_items[_head]);

    /// <summary>Removes every value from the buffer.</summary>
    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    /// <summary>Returns an enumerator from the oldest value to the newest value.</summary>
    /// <returns>An enumerator over the current buffer contents.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        for (var index = 0; index < _count; index++)
        {
            yield return _items[(_head + index) % _items.Length];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

#pragma warning restore CA1711

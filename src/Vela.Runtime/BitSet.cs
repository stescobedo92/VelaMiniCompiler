namespace Vela.Runtime;

/// <summary>Provides packed bit storage with O(1) access to individual bits.</summary>
public sealed class BitSet
{
    private ulong[] _words;

    /// <summary>Initializes a bit set with the requested non-negative bit capacity.</summary>
    /// <param name="capacity">The number of addressable bits.</param>
    public BitSet(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _words = new ulong[WordCount(capacity)];
        Capacity = capacity;
    }

    /// <summary>Gets the number of addressable bits.</summary>
    public int Capacity { get; private set; }

    /// <summary>Sets the bit at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based bit index.</param>
    public void Set(int index)
    {
        ValidateIndex(index);
        _words[index / 64] |= 1UL << (index % 64);
    }

    /// <summary>Clears the bit at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based bit index.</param>
    public void Clear(int index)
    {
        ValidateIndex(index);
        _words[index / 64] &= ~(1UL << (index % 64));
    }

    /// <summary>Determines whether the bit at <paramref name="index"/> is set.</summary>
    /// <param name="index">The zero-based bit index.</param>
    /// <returns><see langword="true"/> when the bit is set; otherwise, <see langword="false"/>.</returns>
    public bool Contains(int index)
    {
        ValidateIndex(index);
        return (_words[index / 64] & (1UL << (index % 64))) != 0;
    }

    /// <summary>Ensures that the bit set can address at least <paramref name="capacity"/> bits.</summary>
    /// <param name="capacity">The requested minimum bit capacity.</param>
    public void Reserve(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity <= Capacity)
        {
            return;
        }

        Array.Resize(ref _words, WordCount(capacity));
        Capacity = capacity;
    }

    /// <summary>Clears every bit while retaining the allocated capacity.</summary>
    public void Clear() => Array.Clear(_words);

    private static int WordCount(int capacity) => checked((capacity + 63) / 64);

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Bit index is outside the configured capacity.");
        }
    }
}

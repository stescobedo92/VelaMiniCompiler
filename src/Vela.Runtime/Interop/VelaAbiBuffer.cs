using System.Runtime.InteropServices;
using System.Text;

namespace Vela.Runtime.Interop;

/// <summary>Owns a flat byte buffer transferred through the Vela ABI v2 boundary.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VelaAbiBuffer
{
    /// <summary>Initializes a buffer from raw wire fields.</summary>
    /// <param name="data">The data pointer.</param>
    /// <param name="length">The number of valid bytes.</param>
    /// <param name="capacity">The allocated capacity in bytes.</param>
    /// <param name="allocatorId">The allocator that owns <paramref name="data"/>.</param>
    public VelaAbiBuffer(nint data, nuint length, nuint capacity, ulong allocatorId)
    {
        Data = data;
        Length = length;
        Capacity = capacity;
        AllocatorId = allocatorId;
    }

    /// <summary>Gets the data pointer. Zero indicates an empty buffer.</summary>
    public nint Data { get; }

    /// <summary>Gets the number of valid bytes.</summary>
    public nuint Length { get; }

    /// <summary>Gets the allocated capacity in bytes.</summary>
    public nuint Capacity { get; }

    /// <summary>Gets the allocator identifier responsible for <see cref="Data"/>.</summary>
    public ulong AllocatorId { get; }

    /// <summary>Copies managed UTF-8 text into a CoTaskMem allocation.</summary>
    /// <param name="value">The text to encode.</param>
    /// <returns>A buffer that owns the encoded bytes.</returns>
    public static VelaAbiBuffer FromUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0)
        {
            return default;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var allocation = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, allocation, bytes.Length);
        return new VelaAbiBuffer(allocation, checked((nuint)bytes.Length), checked((nuint)bytes.Length), allocatorId: 0);
    }

    /// <summary>Decodes the buffer as UTF-8 without transferring ownership.</summary>
    /// <returns>The decoded string, or <see cref="string.Empty"/> when <see cref="Data"/> is zero.</returns>
    public string ToUtf8String()
    {
        if (Data == 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8(Data, checked((int)Length)) ?? string.Empty;
    }

    /// <summary>Releases the CoTaskMem allocation owned by this buffer.</summary>
    public void Free()
    {
        if (Data != 0)
        {
            Marshal.FreeCoTaskMem(Data);
        }
    }
}

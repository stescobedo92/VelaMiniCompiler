using System.Runtime.InteropServices;
using System.Text;

namespace Vela.Runtime;

/// <summary>Owns a UTF-8 text value transferred through Vela's native ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VelaText
{
    /// <summary>Initializes a native text value.</summary>
    public VelaText(nint data, nuint length)
    {
        Data = data;
        Length = length;
    }

    /// <summary>Gets the UTF-8 data pointer.</summary>
    public nint Data { get; }

    /// <summary>Gets the number of UTF-8 data bytes excluding the terminator.</summary>
    public nuint Length { get; }

    /// <summary>Copies managed text into a core-task-memory UTF-8 allocation.</summary>
    public static VelaText FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var allocation = Marshal.AllocCoTaskMem(checked(bytes.Length + 1));
        Marshal.Copy(bytes, 0, allocation, bytes.Length);
        Marshal.WriteByte(allocation, bytes.Length, 0);
        return new VelaText(allocation, checked((nuint)bytes.Length));
    }

    /// <summary>Decodes this UTF-8 value without transferring ownership.</summary>
    public string ToManagedString()
    {
        if (Data == 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUTF8(Data, checked((int)Length)) ?? string.Empty;
    }

    /// <summary>Releases an allocation returned by <see cref="FromString"/>.</summary>
    public static void Free(VelaText value)
    {
        if (value.Data != 0)
        {
            Marshal.FreeCoTaskMem(value.Data);
        }
    }
}

/// <summary>Defines the stable four-word wire representation of a Vela decimal value.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct VelaDecimal
{
    /// <summary>Initializes the raw decimal words.</summary>
    public VelaDecimal(int lo, int mid, int hi, int flags)
    {
        Lo = lo;
        Mid = mid;
        Hi = hi;
        Flags = flags;
    }

    /// <summary>Gets the low 32 bits of the significand.</summary>
    public int Lo { get; }

    /// <summary>Gets the middle 32 bits of the significand.</summary>
    public int Mid { get; }

    /// <summary>Gets the high 32 bits of the significand.</summary>
    public int Hi { get; }

    /// <summary>Gets the sign and scale word.</summary>
    public int Flags { get; }

    /// <summary>Converts a managed decimal to its ABI representation.</summary>
    public static VelaDecimal FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        return new VelaDecimal(bits[0], bits[1], bits[2], bits[3]);
    }

    /// <summary>Converts this ABI representation to a managed decimal.</summary>
    public decimal ToDecimal() => new([Lo, Mid, Hi, Flags]);
}

using System.Runtime.InteropServices;

namespace Vela.Runtime.Interop;

/// <summary>Generation-checked ABI handle referencing a value owned by a handle table.</summary>
/// <param name="Owner">The owning table identifier.</param>
/// <param name="Slot">The slot index inside the owning table.</param>
/// <param name="Generation">The generation observed when the handle was issued.</param>
/// <param name="TypeContract">The type contract bound to the referenced value.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct VelaHandle(ulong Owner, uint Slot, uint Generation, ulong TypeContract);

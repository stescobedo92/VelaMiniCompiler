namespace Vela.Runtime.Interop;

/// <summary>Generation-checked object handle table for ABI v2 ownership.</summary>
public sealed class VelaHandleTable : IDisposable
{
    private readonly ulong _ownerId;
    private readonly object _gate = new();
    private Slot[] _slots = [];
    private readonly Stack<uint> _freeList = new();
    private bool _disposed;

    /// <summary>Creates a handle table scoped to <paramref name="ownerId"/>.</summary>
    /// <param name="ownerId">The owner identifier stamped on issued handles.</param>
    public VelaHandleTable(ulong ownerId)
    {
        _ownerId = ownerId;
    }

    /// <summary>Stores <paramref name="value"/> and returns an owning handle.</summary>
    /// <param name="value">The managed value to store.</param>
    /// <param name="typeContract">The ABI type contract for the stored value.</param>
    /// <returns>A handle referencing the stored value.</returns>
    public VelaHandle Create(object value, ulong typeContract)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            var slotIndex = RentSlotIndex();
            ref var slot = ref _slots[slotIndex];
            slot.Value = value;
            slot.TypeContract = typeContract;
            slot.RefCount = 1;
            slot.IsActive = true;

            return new VelaHandle(_ownerId, slotIndex, slot.Generation, typeContract);
        }
    }

    /// <summary>Resolves a handle to a typed value when ownership and generation are valid.</summary>
    /// <typeparam name="T">The expected managed type.</typeparam>
    /// <param name="handle">The handle to resolve.</param>
    /// <param name="expectedType">The expected ABI type contract.</param>
    /// <returns>The resolution status and value when successful.</returns>
    public (VelaAbiStatus Status, T? Value) Resolve<T>(VelaHandle handle, ulong expectedType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!TryGetActiveSlot(handle, out var slot, out var status))
            {
                return (status, default);
            }

            if (slot.TypeContract != expectedType)
            {
                return (VelaAbiStatus.WrongType, default);
            }

            if (slot.Value is not T typedValue)
            {
                return (VelaAbiStatus.WrongType, default);
            }

            return (VelaAbiStatus.Success, typedValue);
        }
    }

    /// <summary>Increments the reference count for an active handle.</summary>
    /// <param name="handle">The handle to retain.</param>
    /// <returns>The retain status.</returns>
    public VelaAbiResult Retain(VelaHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!TryGetActiveSlotIndex(handle, out var slotIndex, out var status))
            {
                return VelaAbiResult.Fail(status);
            }

            ref var slot = ref _slots[slotIndex];
            slot.RefCount = checked(slot.RefCount + 1);
            return VelaAbiResult.Ok();
        }
    }

    /// <summary>Releases one reference to a handle, recycling the slot when the count reaches zero.</summary>
    /// <param name="handle">The handle to release.</param>
    /// <returns>The release status.</returns>
    public VelaAbiResult Release(VelaHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!TryGetActiveSlotIndex(handle, out var slotIndex, out var status))
            {
                return VelaAbiResult.Fail(status);
            }

            ref var slot = ref _slots[slotIndex];
            slot.RefCount = checked(slot.RefCount - 1);
            if (slot.RefCount > 0)
            {
                return VelaAbiResult.Ok();
            }

            DisposeStoredValue(slot.Value);
            slot.Value = null;
            slot.TypeContract = 0;
            slot.IsActive = false;
            slot.Generation = checked(slot.Generation + 1);
            _freeList.Push(handle.Slot);
            return VelaAbiResult.Ok();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            for (var index = 0; index < _slots.Length; index++)
            {
                ref var slot = ref _slots[index];
                if (!slot.IsActive)
                {
                    continue;
                }

                DisposeStoredValue(slot.Value);
                slot.Value = null;
                slot.IsActive = false;
            }

            _freeList.Clear();
            _disposed = true;
        }
    }

    private uint RentSlotIndex()
    {
        if (_freeList.TryPop(out var slotIndex))
        {
            return slotIndex;
        }

        var nextIndex = (uint)_slots.Length;
        Array.Resize(ref _slots, _slots.Length + 1);
        return nextIndex;
    }

    private bool TryGetActiveSlot(VelaHandle handle, out Slot slot, out VelaAbiStatus status)
    {
        if (!TryGetActiveSlotIndex(handle, out var slotIndex, out status))
        {
            slot = default;
            return false;
        }

        slot = _slots[slotIndex];
        return true;
    }

    private bool TryGetActiveSlotIndex(VelaHandle handle, out uint slotIndex, out VelaAbiStatus status)
    {
        if (handle.Owner != _ownerId)
        {
            slotIndex = 0;
            status = VelaAbiStatus.WrongOwner;
            return false;
        }

        if (handle.Slot >= _slots.Length)
        {
            slotIndex = 0;
            status = VelaAbiStatus.StaleHandle;
            return false;
        }

        ref var slot = ref _slots[handle.Slot];
        if (!slot.IsActive || slot.Generation != handle.Generation)
        {
            slotIndex = 0;
            status = VelaAbiStatus.StaleHandle;
            return false;
        }

        slotIndex = handle.Slot;
        status = VelaAbiStatus.Success;
        return true;
    }

    private static void DisposeStoredValue(object? value)
    {
        switch (value)
        {
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private struct Slot
    {
        public object? Value;
        public ulong TypeContract;
        public uint Generation;
        public uint RefCount;
        public bool IsActive;
    }
}

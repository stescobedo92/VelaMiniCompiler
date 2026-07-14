# Vela.Runtime

`Vela.Runtime` is the small managed support library used by generated Vela applications. It targets `net10.0`, has no package dependencies, and is compatible with Native AOT because it does not use reflection, runtime code generation, or dynamic assembly loading.

## Value flow

`Option<T>` models an optional value, while `Result<T, TError>` models either a successful value or a typed error. Both are readonly value types, so their success-state representation does not allocate.

```csharp
Option<int> count = Option.Some(3);
Result<int, string> parsed = Result.Ok<int, string>(42);
```

## Contracts

Generated bounds checks and language contracts can call `Contract.Require` or `Contract.RequireIndex`. A failed contract produces a `VelaContractException` with the compiler-provided diagnostic text.

## Deterministic disposal

The managed runtime relies on the .NET garbage collector for memory, but resources such as files and sockets still need deterministic cleanup. Generated code can use a `DisposalScope` for reverse-order cleanup:

```csharp
using var scope = new DisposalScope();
FileStream stream = scope.Track(File.OpenRead("input.txt"));
```

`Disposal.DisposeAll` also accepts a span of resources when the compiler already knows the cleanup set statically.

## Collections

The runtime also provides Native AOT-safe collection implementations for Vela:
`VelaVector<T>`, `VelaHashMap<TKey, TValue>`, `VelaHashSet<T>`, `VelaQueue<T>`,
`VelaStack<T>`, `RingBuffer<T>`, and `BitSet`. They use managed arrays or .NET
generic collections, have no reflection dependency, and expose `Option<T>` for
empty removal and lookup operations. The compiler emits calls to these known
APIs rather than dynamically resolving user-provided members.

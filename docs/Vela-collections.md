# Vela collections

Vela collection APIs are small, Native AOT-safe wrappers over contiguous arrays
or .NET generic collections. They borrow the practical guarantees familiar from
C# and Go, but do not claim that .NET and Go share the same private runtime
algorithm.

## Complexity contract

| Type | Representation | Fast operations | Important limitation |
| --- | --- | --- | --- |
| `Vector<T>` | contiguous dynamic array | index read/write O(1); `append` and `pop` O(1) amortized | a growth or `reserve` operation can copy O(n) items |
| `HashMap<K, V>` | hash table | lookup, `set`, `remove` O(1) expected | hash quality and collisions rule out a universal worst-case O(1) claim |
| `HashSet<T>` | hash table | `add`, `contains`, `remove` O(1) expected | the same hashing qualification applies |
| `Queue<T>` | circular queue | `enqueue`, `dequeue`, `peek` O(1) amortized | capacity expansion can copy elements |
| `Stack<T>` | contiguous stack | `push`, `pop`, `peek` O(1) amortized | capacity expansion can copy elements |
| `RingBuffer<T>` | fixed circular array | `try_enqueue`, `dequeue`, `peek` O(1) | it never grows; enqueue returns `false` when full |
| `BitSet` | packed 64-bit words | `set`, `clear`, `contains` O(1) | `reserve` can copy packed words |

`HashMap` and `HashSet` use ordinal equality for `Text`. Hash keys must be a
primitive Vela value, an immutable Vela record, `Option<T>`, or `Result<T, E>`;
mutable collections are rejected as keys by the compiler.

## Construction and vector operations

All generic collection constructors require explicit type arguments. The
optional constructor argument is a non-negative initial capacity. `RingBuffer`
and `BitSet` require a capacity.

```vela
var values = Vector<Int>(128)
values.append(7)
values[0] = 9
values.reserve(256)
let last = values.pop()

if last.has_value:
    print(last.value)
```

`List<T>` remains a compatibility alias for `Vector<T>`, and list literals
create vectors:

```vela
let values: List<Int> = [1, 2, 3]
```

## Hash maps and sets

`HashMap` supports both indexed access and a safe `try_get` lookup. Indexed
reads intentionally mirror C# dictionary behavior and fail for a missing key;
`try_get` is the Vela equivalent of checking presence explicitly.

```vela
var scores = HashMap<Text, Int>(64)
scores.set("Ada", 42)
scores["Grace"] = 36

let ada = scores.try_get("Ada")
if ada.has_value:
    print(ada.value)

var ids = HashSet<Int>()
if ids.add(7):
    print("new id")
```

`HashMap` methods are `set`, `try_get`, `contains`, `remove`, `reserve`, and
`clear`. `HashSet` methods are `add`, `contains`, `remove`, `reserve`, and
`clear`. Both expose `count`.

## Queues, stacks, ring buffers, and bit sets

```vela
var queue = Queue<Text>()
queue.enqueue("first")
let first = queue.dequeue()

var stack = Stack<Int>()
stack.push(7)
let top = stack.pop()

var buffer = RingBuffer<Int>(1024)
let inserted = buffer.try_enqueue(42)
if inserted == false:
    print("buffer full")

var flags = BitSet(256)
flags.set(128)
if flags.contains(128):
    print("enabled")
```

Queue and stack removal and peek operations return `Option<T>`. `RingBuffer`
does not allocate after construction. `BitSet` uses `reserve` to increase its
addressable range; accessing a bit outside `capacity` raises a managed bounds
exception.

## Iteration

Vectors, hash sets, queues, stacks, and ring buffers can be traversed with
`for`. Hash maps and bit sets intentionally do not yet expose a Vela iteration
element type; use their lookup APIs instead.

```vela
for value in values:
    print(value)
```

## Reproducible workloads

The repository contains a small allocation-conscious workload runner at
`benchmarks/Vela.Runtime.Benchmarks`. It records vector appends, hash-map
insertion and lookup, and ring-buffer wraparound without using timing thresholds
as correctness tests.

```powershell
dotnet run --project .\benchmarks\Vela.Runtime.Benchmarks -c Release
```

## Design references

- Microsoft Learn: [`List<T>.Add` complexity](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.add?view=net-10.0)
- Microsoft Learn: [`Dictionary<TKey, TValue>`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)
- Go Blog: [arrays, slices, and append](https://go.dev/blog/slices)
- Go source: [runtime maps](https://go.dev/src/internal/runtime/maps/map.go)

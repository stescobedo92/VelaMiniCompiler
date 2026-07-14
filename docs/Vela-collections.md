# Vela collections

Vela collection APIs are small, Native-AOT-safe wrappers over contiguous arrays
or .NET generic collections. They expose familiar C#/Go-style operations while
documenting the real complexity boundary rather than claiming impossible
worst-case guarantees for hash tables.

## Complexity contract

| Type | Representation | Fast operations | Important limitation |
| --- | --- | --- | --- |
| `Vector<T>` | contiguous dynamic array | index read/write O(1); `append`/`pop` O(1) amortized | growth or `reserve` can copy O(n) items |
| `HashMap<K, V>` | hash table | lookup, `set`, `remove` O(1) expected | collisions and hash quality rule out a universal O(1) bound |
| `HashSet<T>` | hash table | `add`, `contains`, `remove` O(1) expected | same hashing qualification |
| `Queue<T>` | circular queue | `enqueue`, `dequeue`, `peek` O(1) amortized | capacity expansion can copy elements |
| `Stack<T>` | contiguous stack | `push`, `pop`, `peek` O(1) amortized | capacity expansion can copy elements |
| `RingBuffer<T>` | fixed circular array | `try_enqueue`, `dequeue`, `peek` O(1) | never grows; enqueue returns `false` when full |
| `BitSet` | packed 64-bit words | `set`, `clear`, `contains` O(1) | `reserve` can copy packed words |

`HashMap` and `HashSet` use ordinal equality for `Text`. Hash keys must be a
primitive value, immutable record, `Option<T>`, or `Result<T, E>`; mutable
collections are rejected as keys.

## Construction and vector operations

All generic collection constructors require explicit type arguments. The optional
constructor argument is a non-negative capacity. `RingBuffer` and `BitSet`
require a capacity.

```vela
include vela.core;

var values = Vector<Int>(128);
values.append(7);
values[0] = 9;
values.reserve(256);
let last = values.pop();

if last.has_value {
    print(last.value);
}
```

`List<T>` remains a compatibility alias for `Vector<T>` and list literals create
vectors:

```vela
let values: List<Int> = [1, 2, 3];
```

## Hash maps and sets

`HashMap` supports indexed access and safe `try_get`. Indexed reads mirror C#
dictionary behavior and fail for missing keys; `try_get` makes presence explicit.

```vela
var scores = HashMap<Text, Int>(64);
scores.set("Ada", 42);
scores["Grace"] = 36;

let ada = scores.try_get("Ada");
if ada.has_value {
    print(ada.value);
}

var ids = HashSet<Int>();
if ids.add(7) {
    print("new id");
}
```

`HashMap` methods are `set`, `try_get`, `contains`, `remove`, `reserve`, and
`clear`. `HashSet` methods are `add`, `contains`, `remove`, `reserve`, and
`clear`. Both expose `count`.

## Queues, stacks, ring buffers, and bit sets

```vela
var queue = Queue<Text>();
queue.enqueue("first");
let first = queue.dequeue();

var stack = Stack<Int>();
stack.push(7);
let top = stack.pop();

var buffer = RingBuffer<Int>(1024);
let inserted = buffer.try_enqueue(42);
if inserted == false {
    print("buffer full");
}

var flags = BitSet(256);
flags.set(128);
if flags.contains(128) {
    print("enabled");
}
```

Queue and stack removal and peek operations return `Option<T>`. `RingBuffer`
does not allocate after construction. `BitSet` uses `reserve` to increase its
addressable range; accessing a bit outside `capacity` raises a Vela bounds error.

## Iteration

Vectors, hash sets, queues, stacks, and ring buffers support `for`. Hash maps
and bit sets do not yet expose a Vela iteration element type; use their lookup
APIs instead.

```vela
for value in values {
    print(value);
}
```

## Reproducible workloads

The repository includes an allocation-conscious workload runner at
`benchmarks/Vela.Runtime.Benchmarks`. It exercises vector appends, hash-map
insertion/lookup, and ring-buffer wraparound without relying on timing thresholds
for correctness.

```powershell
dotnet run --project .\benchmarks\Vela.Runtime.Benchmarks -c Release
```

## Design references

- Microsoft Learn: [`List<T>.Add` complexity](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.add?view=net-10.0)
- Microsoft Learn: [`Dictionary<TKey, TValue>`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0)
- Go Blog: [arrays, slices, and append](https://go.dev/blog/slices)

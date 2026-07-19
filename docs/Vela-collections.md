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
| `SortedMap<K, V>` | balanced search tree | `set`, `try_get`, `remove` O(log n); `first_key`/`last_key` O(log n) | keys must have a defined ordering |
| `SortedSet<T>` | balanced search tree | `add`, `contains`, `remove` O(log n); iteration is ascending | values must have a defined ordering |
| `Deque<T>` | growable circular array | `push_front`, `push_back`, `pop_front`, `pop_back` O(1) amortized | growth or `reserve` can copy O(n) items |
| `PriorityQueue<T>` | binary min-heap | `push`, `pop` O(log n); `peek` O(1) | serves the smallest value first; values need a defined ordering |
| `LinkedList<T>` | doubly linked list | `push_front`, `push_back`, `pop_front`, `pop_back` O(1) | `contains` and `remove` by value are O(n); nodes are not contiguous |

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

## Ordered collections

`SortedMap`, `SortedSet`, and `PriorityQueue` mirror the ordered containers of
C++ (`std::map`, `std::set`, `std::priority_queue`), Java (`TreeMap`, `TreeSet`,
`PriorityQueue`), and Rust (`BTreeMap`, `BTreeSet`, `BinaryHeap`). Ordering keys
and elements must be a numeric type, `Bool`, or `Text`; `Text` is ordered by
ordinal comparison, so ordering is deterministic across platforms and locales.

`SortedMap` and `SortedSet` take no constructor arguments. `PriorityQueue`
accepts an optional capacity and always serves the smallest value first (a
min-heap). To get max-heap behavior, negate numeric priorities on insert.

```vela
var releases = SortedMap<Text, Int>();
releases.set("0.3.0", 2026);
releases["0.1.0"] = 2025;

let oldest = releases.first_key();
if oldest.has_value {
    print(oldest.value);
}

var levels = SortedSet<Int>();
if levels.add(10) {
    print("new level");
}

var work = PriorityQueue<Int>(64);
work.push(5);
work.push(1);
let urgent = work.pop(); // Some(1): smallest first
```

`SortedMap` methods are `set`, `try_get`, `contains`, `remove`, `first_key`,
`last_key`, and `clear`, plus indexed reads and writes. `SortedSet` methods are
`add`, `contains`, `remove`, `first`, `last`, and `clear`. `PriorityQueue`
methods are `push`, `pop`, `peek`, `reserve`, and `clear`.

## Deques and linked lists

`Deque` mirrors C++ `std::deque`, Java `ArrayDeque`, and Rust `VecDeque`;
`LinkedList` mirrors C++ `std::list`, Java `LinkedList`, and Rust `LinkedList`.

```vela
var window = Deque<Text>();
window.push_back("b");
window.push_front("a");
let front = window.pop_front(); // Some("a")
let back = window.peek_back();  // Some("b")

var history = LinkedList<Text>();
history.push_back("open");
history.push_back("save");
if history.remove("open") {
    print("removed");
}
```

`Deque` methods are `push_front`, `push_back`, `pop_front`, `pop_back`,
`peek_front`, `peek_back`, `reserve`, and `clear`. `LinkedList` methods are
`push_front`, `push_back`, `pop_front`, `pop_back`, `peek_front`, `peek_back`,
`contains`, `remove`, and `clear`. Prefer `Deque` unless you specifically need
O(1) removal of already-located nodes; contiguous storage is faster in practice.

## Iteration

Vectors, hash sets, sorted sets, queues, stacks, ring buffers, deques, and
linked lists support `for`. Sorted sets iterate in ascending order; deques and
linked lists iterate from front to back. Hash maps, sorted maps, priority
queues, and bit sets do not yet expose a Vela iteration element type; use their
lookup APIs instead.

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

using System.Diagnostics;
using Vela.Runtime;

const int Iterations = 1_000_000;

Console.WriteLine($"Vela runtime collection workloads ({Iterations:N0} iterations)");
Measure("Vector append", BenchmarkVectorAppend);
Measure("HashMap set and lookup", BenchmarkHashMapSetAndLookup);
Measure("RingBuffer enqueue and dequeue", BenchmarkRingBuffer);

static void Measure(string name, Func<long> workload)
{
    _ = workload();
    var timer = Stopwatch.StartNew();
    var checksum = workload();
    timer.Stop();
    GC.KeepAlive(checksum);
    Console.WriteLine($"{name}: {timer.ElapsedMilliseconds} ms (checksum {checksum})");
}

static long BenchmarkVectorAppend()
{
    var values = new VelaVector<long>(Iterations);
    for (var index = 0; index < Iterations; index++)
    {
        values.Append(index);
    }

    return values.Count + values[Iterations - 1];
}

static long BenchmarkHashMapSetAndLookup()
{
    var values = new VelaHashMap<long, long>(Iterations);
    for (var index = 0; index < Iterations; index++)
    {
        values.Set(index, index);
    }

    long checksum = 0;
    for (var index = 0; index < Iterations; index++)
    {
        checksum += values.TryGet(index).Value;
    }

    return checksum;
}

static long BenchmarkRingBuffer()
{
    var values = new RingBuffer<long>(1024);
    long checksum = 0;
    for (var index = 0; index < Iterations; index++)
    {
        if (!values.TryEnqueue(index))
        {
            checksum += values.Dequeue().Value;
            _ = values.TryEnqueue(index);
        }
    }

    while (values.Dequeue().TryGetValue(out var value))
    {
        checksum += value;
    }

    return checksum;
}

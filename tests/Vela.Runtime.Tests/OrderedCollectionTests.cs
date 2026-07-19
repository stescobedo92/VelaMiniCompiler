using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class OrderedCollectionTests
{
    [Fact]
    public void SortedMap_OrdersKeysAndUsesOptionSemantics()
    {
        var map = new VelaSortedMap<string, int>();
        map.Set("mira", 91);
        map["ada"] = 97;
        map.Set("zoe", 84);

        Assert.Equal(3, map.Count);
        Assert.Equal("ada", map.FirstKey().Value);
        Assert.Equal("zoe", map.LastKey().Value);
        Assert.Equal(97, map.TryGet("ada").Value);
        Assert.True(map.TryGet("unknown").IsNone);
        Assert.True(map.Contains("mira"));
        Assert.True(map.Remove("mira"));
        Assert.False(map.Contains("mira"));

        map.Clear();
        Assert.Equal(0, map.Count);
        Assert.True(map.FirstKey().IsNone);
        Assert.True(map.LastKey().IsNone);
    }

    [Fact]
    public void SortedSet_IteratesInAscendingOrder()
    {
        var set = new VelaSortedSet<int>();

        Assert.True(set.Add(30));
        Assert.True(set.Add(10));
        Assert.True(set.Add(20));
        Assert.False(set.Add(20));

        Assert.Equal(3, set.Count);
        Assert.Equal([10, 20, 30], set.ToArray());
        Assert.Equal(10, set.First().Value);
        Assert.Equal(30, set.Last().Value);
        Assert.True(set.Remove(10));
        Assert.False(set.Contains(10));

        set.Clear();
        Assert.True(set.First().IsNone);
        Assert.True(set.Last().IsNone);
    }

    [Fact]
    public void Deque_SupportsBothEndsAndWrapsAcrossGrowth()
    {
        var deque = new VelaDeque<int>(2);

        deque.PushBack(2);
        deque.PushFront(1);
        deque.PushBack(3);
        deque.PushFront(0);

        Assert.Equal(4, deque.Count);
        Assert.Equal([0, 1, 2, 3], deque.ToArray());
        Assert.Equal(0, deque.PeekFront().Value);
        Assert.Equal(3, deque.PeekBack().Value);
        Assert.Equal(0, deque.PopFront().Value);
        Assert.Equal(3, deque.PopBack().Value);
        Assert.Equal([1, 2], deque.ToArray());

        deque.Reserve(32);
        Assert.True(deque.Capacity >= 32);
        Assert.Equal([1, 2], deque.ToArray());

        deque.Clear();
        Assert.Equal(0, deque.Count);
        Assert.True(deque.PopFront().IsNone);
        Assert.True(deque.PopBack().IsNone);
        Assert.True(deque.PeekFront().IsNone);
        Assert.True(deque.PeekBack().IsNone);
    }

    [Fact]
    public void PriorityQueue_ServesSmallestValueFirst()
    {
        var queue = new VelaPriorityQueue<int>(4);

        queue.Push(5);
        queue.Push(1);
        queue.Push(3);

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.Peek().Value);
        Assert.Equal(1, queue.Pop().Value);
        Assert.Equal(3, queue.Pop().Value);
        Assert.Equal(5, queue.Pop().Value);
        Assert.True(queue.Pop().IsNone);
        Assert.True(queue.Peek().IsNone);

        queue.Reserve(16);
        Assert.True(queue.Capacity >= 16);
    }

    [Fact]
    public void PriorityQueue_UsesOrdinalOrderingForText()
    {
        var queue = new VelaPriorityQueue<string>();

        queue.Push("zoe");
        queue.Push("Ada");
        queue.Push("ada");

        Assert.Equal("Ada", queue.Pop().Value);
        Assert.Equal("ada", queue.Pop().Value);
        Assert.Equal("zoe", queue.Pop().Value);
    }

    [Fact]
    public void LinkedList_SupportsEndsAndValueRemoval()
    {
        var list = new VelaLinkedList<string>();

        list.PushBack("edit");
        list.PushFront("open");
        list.PushBack("save");

        Assert.Equal(3, list.Count);
        Assert.Equal(["open", "edit", "save"], list.ToArray());
        Assert.Equal("open", list.PeekFront().Value);
        Assert.Equal("save", list.PeekBack().Value);
        Assert.True(list.Contains("edit"));
        Assert.True(list.Remove("edit"));
        Assert.False(list.Remove("edit"));
        Assert.Equal("open", list.PopFront().Value);
        Assert.Equal("save", list.PopBack().Value);
        Assert.True(list.PopFront().IsNone);
        Assert.True(list.PopBack().IsNone);
    }
}

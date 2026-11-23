namespace ServiceDesk.Domain.Algorithms;

/// <summary>
/// Top-down Merge Sort with a single auxiliary buffer.
/// Time: Best/Average/Worst Θ(n log n). Space: Θ(n) auxiliary + O(log n) recursion stack.
/// Stable: equal keys keep their input order → deterministic queue order for ties.
/// Chosen over Quick Sort because stability and a guaranteed worst case matter more than
/// in-place sorting for a queue of at most a few thousand open tickets.
/// </summary>
public static class MergeSort
{
    public static T[] Sort<T>(IReadOnlyList<T> source, IComparer<T> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(comparer);

        var items = new T[source.Count];
        for (var i = 0; i < items.Length; i++) items[i] = source[i];
        if (items.Length < 2) return items;

        var buffer = new T[items.Length];
        SortRange(items, buffer, 0, items.Length - 1, comparer);
        return items;
    }

    private static void SortRange<T>(T[] a, T[] buf, int lo, int hi, IComparer<T> cmp)
    {
        if (hi - lo < 16) { InsertionSort(a, lo, hi, cmp); return; }   // small-range cutoff (stable)

        var mid = lo + (hi - lo) / 2;
        SortRange(a, buf, lo, mid, cmp);
        SortRange(a, buf, mid + 1, hi, cmp);
        if (cmp.Compare(a[mid], a[mid + 1]) <= 0) return;               // already ordered → best case O(n)
        Merge(a, buf, lo, mid, hi, cmp);
    }

    private static void Merge<T>(T[] a, T[] buf, int lo, int mid, int hi, IComparer<T> cmp)
    {
        Array.Copy(a, lo, buf, lo, hi - lo + 1);
        int i = lo, j = mid + 1;
        for (var k = lo; k <= hi; k++)
        {
            if (i > mid) a[k] = buf[j++];
            else if (j > hi) a[k] = buf[i++];
            else if (cmp.Compare(buf[j], buf[i]) < 0) a[k] = buf[j++];  // strict < keeps stability
            else a[k] = buf[i++];
        }
    }

    private static void InsertionSort<T>(T[] a, int lo, int hi, IComparer<T> cmp)
    {
        for (var i = lo + 1; i <= hi; i++)
        {
            var key = a[i];
            var j = i - 1;
            while (j >= lo && cmp.Compare(a[j], key) > 0) { a[j + 1] = a[j]; j--; }
            a[j + 1] = key;
        }
    }
}

/// <summary>
/// Array-backed binary min-heap ordered by an <see cref="IComparer{T}"/>
/// (the element that compares "smallest" is served first).
/// Build (Floyd heapify): O(n). Enqueue / Dequeue: O(log n). Peek: O(1). Space: O(n).
/// </summary>
public sealed class BinaryHeapPriorityQueue<T>
{
    private readonly List<T> _heap;
    private readonly IComparer<T> _comparer;

    public BinaryHeapPriorityQueue(IComparer<T> comparer, IEnumerable<T>? items = null)
    {
        _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
        _heap = items?.ToList() ?? new List<T>();
        for (var i = _heap.Count / 2 - 1; i >= 0; i--) SiftDown(i);   // Floyd: Σ heights = O(n)
    }

    public int Count => _heap.Count;

    public void Enqueue(T item)
    {
        _heap.Add(item);
        SiftUp(_heap.Count - 1);
    }

    public T Peek() => _heap.Count > 0 ? _heap[0] : throw new InvalidOperationException("Queue is empty.");

    public bool TryDequeue(out T item)
    {
        if (_heap.Count == 0) { item = default!; return false; }
        item = _heap[0];
        var last = _heap.Count - 1;
        _heap[0] = _heap[last];
        _heap.RemoveAt(last);
        if (_heap.Count > 0) SiftDown(0);
        return true;
    }

    public T Dequeue() => TryDequeue(out var item) ? item : throw new InvalidOperationException("Queue is empty.");

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_comparer.Compare(_heap[i], _heap[parent]) >= 0) break;
            (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        var n = _heap.Count;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, best = i;
            if (l < n && _comparer.Compare(_heap[l], _heap[best]) < 0) best = l;
            if (r < n && _comparer.Compare(_heap[r], _heap[best]) < 0) best = r;
            if (best == i) return;
            (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
            i = best;
        }
    }
}

/// <summary>
/// Quickselect (Hoare's selection) with random pivot for order statistics (median).
/// Time: Best/Average O(n), Worst O(n²) (vanishingly unlikely with random pivots). Space: O(n) copy.
/// </summary>
public static class QuickSelect
{
    // Thread-safe shared RNG; the selected value is identical whatever pivots are drawn.
    private static Random Rng => Random.Shared;

    public static double Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var a = values.ToArray();
        var n = a.Length;
        var upper = Select(a, n / 2);
        if (n % 2 == 1) return upper;
        // After selecting index n/2, a[0..n/2-1] are all <= upper, so the lower median is their max: O(n).
        var lower = double.MinValue;
        for (var i = 0; i < n / 2; i++) if (a[i] > lower) lower = a[i];
        return (lower + upper) / 2.0;
    }

    /// <summary>Returns the k-th smallest (0-based) element; partially reorders <paramref name="a"/>.</summary>
    public static double Select(double[] a, int k)
    {
        if (k < 0 || k >= a.Length) throw new ArgumentOutOfRangeException(nameof(k));
        int lo = 0, hi = a.Length - 1;
        while (lo < hi)
        {
            var pivotIndex = Partition(a, lo, hi, lo + Rng.Next(hi - lo + 1));
            if (k == pivotIndex) return a[k];
            if (k < pivotIndex) hi = pivotIndex - 1; else lo = pivotIndex + 1;
        }
        return a[lo];
    }

    private static int Partition(double[] a, int lo, int hi, int pivotIndex)
    {
        var pivot = a[pivotIndex];
        (a[pivotIndex], a[hi]) = (a[hi], a[pivotIndex]);
        var store = lo;
        for (var i = lo; i < hi; i++)
            if (a[i] < pivot) { (a[i], a[store]) = (a[store], a[i]); store++; }
        (a[store], a[hi]) = (a[hi], a[store]);
        return store;
    }
}

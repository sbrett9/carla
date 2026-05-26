// Source: carla/road/RoadElementSet.h
//
// A sorted-by-distance container of RoadInfo records (or anything with a
// Distance / GetDistance() value). Upstream uses a sorted std::vector<T> with
// binary-search queries by `s`. We keep the same shape: a List<T> sorted on
// insertion-finalize, plus reverse / range subsetting.
//
// Wave 3 will hit this for "give me the RoadInfo<T> active at distance s" —
// which is the inner loop of GetWidth, GetElevation, GetLaneOffset, etc.
namespace CarlaNet.Map.Road;

/// <summary>
/// Generic sorted set of road elements keyed by a double distance "s". Items
/// must either be doubles themselves or expose a <c>Distance</c>-ish key via the
/// supplied key selector.
/// </summary>
public sealed class RoadElementSet<T>
{
    private readonly List<T> _items;
    private readonly System.Func<T, double> _keyOf;

    public RoadElementSet()
        : this(System.Array.Empty<T>(), DefaultKey) { }

    public RoadElementSet(IEnumerable<T> source)
        : this(source, DefaultKey) { }

    public RoadElementSet(IEnumerable<T> source, System.Func<T, double> keyOf)
    {
        _keyOf = keyOf;
        _items = new List<T>(source);
        _items.Sort((a, b) => _keyOf(a).CompareTo(_keyOf(b)));
    }

    private static double DefaultKey(T value)
    {
        // T may be RoadInfo (has Distance) or double itself.
        if (value is Element.RoadInfo info) return info.Distance;
        if (value is double d) return d;
        throw new System.InvalidOperationException(
            $"RoadElementSet<{typeof(T).Name}> needs an explicit key selector — value type does not expose a distance.");
    }

    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    /// <summary>All records, ordered by ascending s.</summary>
    public IReadOnlyList<T> All => _items;

    /// <summary>
    /// Returns all records with key &lt;= s, in DESCENDING order of s. This matches
    /// upstream <c>GetReverseSubset</c>; iterators stop at the first record so callers
    /// can take "the active record at s" by taking the first element.
    /// </summary>
    public IEnumerable<T> GetReverseSubset(double s)
    {
        int upper = UpperBound(s);
        for (int i = upper - 1; i >= 0; --i) yield return _items[i];
    }

    /// <summary>Records with min &lt;= key &lt;= max (inclusive), ascending.</summary>
    public IEnumerable<T> GetSubsetInRange(double minS, double maxS)
    {
        int lo = LowerBound(minS);
        int hi = UpperBound(maxS);
        for (int i = lo; i < hi; ++i) yield return _items[i];
    }

    /// <summary>Records in [min, max], in DESCENDING order.</summary>
    public IEnumerable<T> GetReverseSubsetInRange(double minS, double maxS)
    {
        int lo = LowerBound(minS);
        int hi = UpperBound(maxS);
        for (int i = hi - 1; i >= lo; --i) yield return _items[i];
    }

    // Standard lower/upper-bound binary search on the sorted key.
    private int LowerBound(double s)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_keyOf(_items[mid]) < s) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private int UpperBound(double s)
    {
        int lo = 0, hi = _items.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_keyOf(_items[mid]) <= s) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}

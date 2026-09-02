using System.Collections;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The list <see cref="FakeFoundryGateApiClient"/> records what a page asked for in — a
/// <see cref="List{T}"/> in every respect a test cares about, except that reading it from one
/// thread while the component under test writes to it from another is defined behaviour.
/// </summary>
/// <remarks>
/// <b>Why this exists (#203).</b> A page's API calls happen on the renderer's thread — a debounced
/// search fires from a timer, <c>/dashboard</c>'s refresh loop fires from a
/// <see cref="PeriodicTimer"/> — while <c>WaitForAssertion</c> evaluates its assertion on the test's
/// thread. A plain <see cref="List{T}"/> being appended to mid-enumeration throws
/// <see cref="InvalidOperationException"/> ("collection was modified"), and a plain
/// <see cref="Dictionary{TKey,TValue}"/> being written to mid-read can do considerably worse. Those
/// exceptions surfaced as the timeouts #203 was filed about: <c>WaitForAssertion</c> swallows a
/// failing assertion and retries, so an assertion that throws on every attempt looks exactly like a
/// condition that never became true.
/// <para>
/// Every read happens under the lock — a snapshot for the enumerating ones, a single acquisition for
/// the indexers — so an assertion sees one consistent moment rather than a list mutating underneath
/// it.
/// </para>
/// </remarks>
public sealed class RecordedCalls<T> : IReadOnlyList<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <inheritdoc />
    public T this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _items[index];
            }
        }
    }

    /// <summary>
    /// One lock for <c>[^1]</c>, which is how nearly every assertion here reads "the last call the
    /// page made". Without this overload the compiler lowers <c>[^1]</c> to <c>Count</c> followed by
    /// <c>this[int]</c> — two lock acquisitions with a gap in between, so the one read the class
    /// promises is consistent would be the one that isn't (#177 review).
    /// </summary>
    public T this[Index index]
    {
        get
        {
            lock (_gate)
            {
                return _items[index.GetOffset(_items.Count)];
            }
        }
    }

    /// <summary>Records one call. Called from whichever thread the component happened to be on.</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _items.Add(item);
        }
    }

    /// <summary>Forgets everything recorded so far — for a test that arranges, clears, then acts.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>A point-in-time copy — what every read here is served from.</summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

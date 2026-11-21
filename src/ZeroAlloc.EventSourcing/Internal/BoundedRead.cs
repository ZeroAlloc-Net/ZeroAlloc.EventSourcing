using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing.Internal;

/// <summary>
/// The fallback behind the default implementations of the bounded read overloads: it ends the
/// unbounded read after <c>maxCount</c> items. An implementation that can bound the read at the
/// source, such as a SQL adapter with <c>LIMIT</c>, overrides the overload instead.
/// </summary>
internal static class BoundedRead
{
    /// <summary>The first <paramref name="maxCount"/> events of <paramref name="source"/>, enumerated directly.</summary>
    public static async IAsyncEnumerable<EventEnvelope> Take(
        EventStreamReader source,
        int maxCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A token that cannot be cancelled would replace one the source was configured with.
        if (ct.CanBeCanceled)
            source = source.WithCancellation(ct);

        var count = 0;
        await foreach (var item in source.ConfigureAwait(false))
        {
            yield return item;
            if (++count >= maxCount)
                yield break;
        }
    }

    public static async IAsyncEnumerable<T> Take<T>(
        IAsyncEnumerable<T> source,
        int maxCount,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = 0;
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return item;
            if (++count >= maxCount)
                yield break;
        }
    }
}

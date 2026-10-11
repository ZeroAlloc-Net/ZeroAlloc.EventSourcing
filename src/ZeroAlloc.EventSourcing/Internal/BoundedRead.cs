using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing.Internal;

/// <summary>
/// The fallback behind the default implementations of the bounded read overloads: it ends the
/// unbounded read after <c>maxCount</c> items. An implementation that can bound the read at the
/// source, such as a SQL adapter with <c>LIMIT</c>, overrides the overload instead.
/// </summary>
internal static class BoundedRead
{
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

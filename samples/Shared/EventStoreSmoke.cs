// samples/Shared/EventStoreSmoke.cs
// The event store round trip the PostgreSQL and SQL Server smokes run against their adapter:
// append, read back, a rejected append on a stale version, and a subscription that delivers.
namespace ZeroAlloc.EventSourcing.AotSmoke.Shared;

/// <summary>Runs an <see cref="EventStore"/> over a real adapter and checks what comes back.</summary>
public static class EventStoreSmoke
{
    private static readonly TimeSpan SubscriptionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the round trip; throws <see cref="SmokeFailure"/> on the first wrong result.</summary>
    public static async Task RunAsync(IEventStoreAdapter adapter, string label)
    {
        var store = new EventStore(adapter, new PingSerializer(), new PingRegistry());
        var stream = new StreamId(Smoke.Unique("smoke"));

        var events = new object[] { new Ping(1), new Ping(2), new Ping(3) };
        var appended = await store.AppendAsync(stream, events.AsMemory(), StreamPosition.Start).ConfigureAwait(false);
        Smoke.Check(appended.IsSuccess, $"{label}: append returned {(appended.IsSuccess ? "" : appended.Error.ToString())}");

        var read = new List<(long Position, Ping Event)>();
        await foreach (var envelope in store.ReadAsync(stream, StreamPosition.Start).ConfigureAwait(false))
        {
            Smoke.Check(envelope.Event is Ping, $"{label}: expected a Ping, got {envelope.Event}");
            read.Add((envelope.Position.Value, (Ping)envelope.Event));
        }
        Smoke.Check(
            read.Count == 3
                && read[0] == (1, new Ping(1))
                && read[1] == (2, new Ping(2))
                && read[2] == (3, new Ping(3)),
            $"{label}: read back {string.Join(", ", read)}, expected Ping 1..3 at positions 1..3");

        var stale = await store.AppendAsync(stream, new object[] { new Ping(4) }.AsMemory(), StreamPosition.Start)
            .ConfigureAwait(false);
        Smoke.Check(
            !stale.IsSuccess && string.Equals(stale.Error.Code, "CONFLICT", StringComparison.Ordinal),
            $"{label}: an append at a stale version returned {(stale.IsSuccess ? "success" : stale.Error.ToString())}, expected CONFLICT");

        var delivered = 0;
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await store.SubscribeAsync(stream, StreamPosition.Start, (envelope, _) =>
        {
            if (envelope.Event is Ping && Interlocked.Increment(ref delivered) == 3)
                allDelivered.TrySetResult();
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        await subscription.StartAsync().ConfigureAwait(false);

        var finished = await Task.WhenAny(allDelivered.Task, Task.Delay(SubscriptionTimeout)).ConfigureAwait(false);
        Smoke.Check(
            finished == allDelivered.Task,
            $"{label}: the subscription delivered {Volatile.Read(ref delivered)} of 3 events within {SubscriptionTimeout.TotalSeconds:0} s");
    }
}

using System.Text;
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Tests;

/// <summary>
/// Unit tests for <see cref="PollingEventSubscription"/> using an inline fake adapter —
/// no Testcontainers required.
/// </summary>
public sealed class PollingEventSubscriptionTests
{
    private static RawEvent MakeRaw(string eventType = "TestEvent")
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        return new RawEvent(new StreamPosition(1), eventType, bytes.AsMemory(), EventMetadata.New(eventType));
    }

    /// <summary>
    /// Fake adapter that yields a single pre-built event on the first ReadAsync call.
    /// Subsequent calls yield nothing, simulating an empty live tail.
    /// </summary>
    private sealed class SingleEventAdapter : IEventStoreAdapter
    {
        private readonly RawEvent _event;
        private int _readCount;

        public SingleEventAdapter(RawEvent @event) => _event = @event;

        public async IAsyncEnumerable<RawEvent> ReadAsync(
            StreamId id,
            StreamPosition from,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_readCount++ == 0)
                yield return _event;
        }

        public ValueTask<Result<AppendResult, StoreError>> AppendAsync(
            StreamId id, ReadOnlyMemory<RawEvent> events, StreamPosition expectedVersion, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<IEventSubscription> SubscribeAsync(
            StreamId id, StreamPosition from, Func<RawEvent, CancellationToken, ValueTask> handler, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task HandlerException_PropagatesViaDisposeAsync()
    {
        var id = new StreamId("test-stream");
        var adapter = new SingleEventAdapter(MakeRaw());

        var sub = new PollingEventSubscription(
            adapter, id, StreamPosition.Start,
            (_, _) => ValueTask.FromException(new InvalidOperationException("handler boom")),
            TimeSpan.FromMilliseconds(50));

        await sub.StartAsync();

        // Give the background task a moment to hit the handler and fault.
        await Task.Delay(200);

        var act = async () => await sub.DisposeAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler boom");
    }
}

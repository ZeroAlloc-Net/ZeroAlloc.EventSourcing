using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// The events of one read of an <see cref="IEventStore"/>, returned by
/// <see cref="IEventStore.ReadAsync"/> and <see cref="IEventStore.ReadBatchAsync"/>.
/// Enumerate it with <c>await foreach</c>.
/// </summary>
/// <remarks>
/// <para>
/// The reader is a struct with a struct enumerator that <c>await foreach</c> binds to by pattern, so a
/// read of an <see cref="EventStore"/> allocates nothing in the store. It holds only the arguments of
/// the read: each enumeration starts the read afresh, so the reader can be enumerated again, or by
/// several callers at once, like the <see cref="IAsyncEnumerable{T}"/> it replaces.
/// </para>
/// <para>
/// <see cref="ConfigureAwait"/> and <see cref="WithCancellation"/> work as they do on an
/// <see cref="IAsyncEnumerable{T}"/>. Code that needs an <see cref="IAsyncEnumerable{T}"/>, such as
/// LINQ, calls <see cref="AsAsyncEnumerable"/>, which costs one object per call.
/// </para>
/// <para>
/// An <see cref="IEventStore"/> implementation other than <see cref="EventStore"/>, such as a
/// decorator or a test double, returns the reader of the store it wraps, or wraps its own events with
/// <see cref="EventStreamReader(IAsyncEnumerable{EventEnvelope})"/>.
/// </para>
/// </remarks>
public readonly struct EventStreamReader
{
    private readonly EventStore? _store;
    private readonly IAsyncEnumerable<EventEnvelope>? _source;
    private readonly StreamId _id;
    private readonly StreamPosition _from;
    private readonly int _maxCount;
    private readonly CancellationToken _ct;
    private readonly CancellationToken _enumeratorToken;

    /// <summary>Wraps <paramref name="source"/>, for an <see cref="IEventStore"/> that produces its own events.</summary>
    /// <param name="source">The events of the read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public EventStreamReader(IAsyncEnumerable<EventEnvelope> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <summary>A read of <paramref name="store"/>; <paramref name="maxCount"/> is zero for the whole stream.</summary>
    internal EventStreamReader(EventStore store, StreamId id, StreamPosition from, int maxCount, CancellationToken ct)
    {
        _store = store;
        _id = id;
        _from = from;
        _maxCount = maxCount;
        _ct = ct;
    }

    private EventStreamReader(in EventStreamReader reader, CancellationToken enumeratorToken)
    {
        _store = reader._store;
        _source = reader._source;
        _id = reader._id;
        _from = reader._from;
        _maxCount = reader._maxCount;
        _ct = reader._ct;
        _enumeratorToken = enumeratorToken;
    }

    /// <summary>Starts an enumeration of the read.</summary>
    /// <param name="cancellationToken">
    /// Cancels this enumeration, together with the token the read was started with. When it cannot be
    /// cancelled, the token from <see cref="WithCancellation"/> is used instead.
    /// </param>
    /// <returns>An enumerator that <c>await foreach</c> disposes when it is done.</returns>
    public Enumerator GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
            cancellationToken = _enumeratorToken;

        if (_store is { } store)
            return store.Enumerate(_id, _from, _maxCount, _ct, cancellationToken);

        return _source is { } source
            ? new Enumerator(source.GetAsyncEnumerator(cancellationToken))
            : default;
    }

    /// <summary>Configures how the awaits of an <c>await foreach</c> over the reader resume.</summary>
    /// <param name="continueOnCapturedContext">
    /// <see langword="false"/> to resume without the captured synchronization context, as library code does.
    /// </param>
    /// <returns>The reader, configured.</returns>
    public ConfiguredEventStreamReader ConfigureAwait(bool continueOnCapturedContext)
        => new(this, continueOnCapturedContext);

    /// <summary>Sets the token an <c>await foreach</c> over the reader enumerates with.</summary>
    /// <param name="cancellationToken">Cancels the enumeration, together with the token the read was started with.</param>
    /// <returns>The reader, with the token.</returns>
    public EventStreamReader WithCancellation(CancellationToken cancellationToken)
        => new(this, cancellationToken);

    /// <summary>
    /// The read as an <see cref="IAsyncEnumerable{T}"/>, for code that needs one, such as LINQ. Costs one
    /// object per call; <c>await foreach</c> over the reader itself costs none.
    /// </summary>
    /// <returns>The events of the read.</returns>
    /// <remarks>
    /// A token set with <see cref="WithCancellation"/> carries over: it cancels every enumeration of the
    /// result that does not pass a token of its own.
    /// </remarks>
    public IAsyncEnumerable<EventEnvelope> AsAsyncEnumerable()
    {
        if (_store is null && !_enumeratorToken.CanBeCanceled)
            return _source ?? AsyncEnumerable.Empty<EventEnvelope>();

        return Iterate(this);
    }

    private static async IAsyncEnumerable<EventEnvelope> Iterate(
        EventStreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // GetAsyncEnumerator falls back to the reader's own token when this one cannot be cancelled.
        var enumerator = reader.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                yield return enumerator.Current;
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The enumerator of an <see cref="EventStreamReader"/>.</summary>
    /// <remarks>
    /// Use each enumerator once, from one caller at a time, as with any async enumerator. A copy that is
    /// used after the enumeration was disposed reads nothing: <see cref="MoveNextAsync"/> returns
    /// <see langword="false"/>.
    /// </remarks>
    public readonly struct Enumerator : IAsyncDisposable
    {
        private readonly EventStore.ReadState? _state;
        private readonly int _version;
        private readonly IAsyncEnumerator<EventEnvelope>? _source;

        internal Enumerator(EventStore.ReadState state, int version)
        {
            _state = state;
            _version = version;
        }

        internal Enumerator(IAsyncEnumerator<EventEnvelope> source) => _source = source;

        /// <summary>The event the enumerator is at.</summary>
        public EventEnvelope Current
        {
            get
            {
                if (_state is { } state)
                    return state.Current(_version);

                return _source is { } source ? source.Current : default;
            }
        }

        /// <summary>Advances to the next event.</summary>
        /// <returns><see langword="true"/> when the enumerator is at an event; <see langword="false"/> at the end of the read.</returns>
        public ValueTask<bool> MoveNextAsync()
        {
            if (_state is { } state)
                return state.MoveNextAsync(_version);

            return _source is { } source ? source.MoveNextAsync() : new ValueTask<bool>(false);
        }

        /// <summary>Ends the enumeration and releases what the read holds, such as a database connection.</summary>
        /// <returns>A task that completes when the read is released.</returns>
        public ValueTask DisposeAsync()
        {
            if (_state is { } state)
                return state.DisposeAsync(_version);

            return _source is { } source ? source.DisposeAsync() : default;
        }
    }
}

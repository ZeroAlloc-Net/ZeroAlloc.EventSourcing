using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing;

public sealed partial class EventStore
{
    /// <summary>
    /// Starts an enumeration of <see cref="ReadAsync"/> or <see cref="ReadBatchAsync"/>: rents a
    /// <see cref="ReadState"/> and points it at the read's arguments.
    /// </summary>
    internal EventStreamReader.Enumerator Enumerate(
        StreamId id, StreamPosition from, int maxCount, CancellationToken readToken, CancellationToken enumeratorToken)
    {
        var state = ReadState.Rent();
        var version = state.Start(this, id, from, maxCount, readToken, enumeratorToken);
        return new EventStreamReader.Enumerator(state, version);
    }

    /// <summary>
    /// The state of one enumeration of an <see cref="EventStreamReader"/> over this store: the
    /// adapter's enumerator, the deserialized event and, for a bounded read, the paging counters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state outlives the awaits inside <see cref="MoveNextAsync"/>, so it is an object, not a
    /// field of the struct enumerator. It is pooled per thread, up to <see cref="PoolSize"/> states, so
    /// a read allocates nothing in the store once a thread has read before, also when reads nest, as
    /// a projection that reads another stream while it handles an event does.
    /// <see cref="DisposeAsync"/> returns it to the pool.
    /// </para>
    /// <para>
    /// Every rental gets a new version, and the struct enumerator carries the version it was handed.
    /// A copy of the enumerator that is used after its enumeration was disposed carries an old version:
    /// it finds nothing to read and never touches the enumeration that rented the state since. That
    /// is what makes the pooling safe; without the version a stale enumerator would silently read
    /// another caller's stream.
    /// </para>
    /// <para>
    /// <see cref="MoveNextAsync"/> uses <see cref="PoolingAsyncValueTaskMethodBuilder{TResult}"/>, so
    /// a step that completes synchronously allocates nothing and one that has to wait for the adapter
    /// borrows a pooled box.
    /// </para>
    /// </remarks>
    internal sealed class ReadState
    {
        /// <summary>How many returned states a thread keeps: the nesting depth that reads without allocating.</summary>
        internal const int PoolSize = 4;

        [ThreadStatic]
        private static ReadState?[]? t_pool;

        private EventStore? _store;
        private StreamId _id;
        private StreamPosition _from;
        private StreamPosition _pageStart;
        private int _maxCount;
        private int _requested;
        private int _rowsInPage;
        private int _yielded;
        private CancellationToken _token;
        private CancellationTokenSource? _linkedSource;
        private IAsyncEnumerator<RawEvent>? _rows;
        private object? _event;
        private bool _hasCurrent;
        private bool _running;
        private int _version;

        public static ReadState Rent()
        {
            if (t_pool is { } pool)
            {
                for (var i = 0; i < pool.Length; i++)
                {
                    if (pool[i] is { } state)
                    {
                        pool[i] = null;
                        return state;
                    }
                }
            }

            return new ReadState();
        }

        private static void Return(ReadState state)
        {
            var pool = t_pool ??= new ReadState?[PoolSize];
            for (var i = 0; i < pool.Length; i++)
            {
                if (pool[i] is null)
                {
                    pool[i] = state;
                    return;
                }
            }
        }

        /// <summary>
        /// Points the state at a read. <paramref name="maxCount"/> is zero for a read of the whole
        /// stream. Returns the version the enumerator has to present.
        /// </summary>
        public int Start(
            EventStore store, StreamId id, StreamPosition from, int maxCount,
            CancellationToken readToken, CancellationToken enumeratorToken)
        {
            _store = store;
            _id = id;
            _from = from;
            _maxCount = maxCount;
            _running = true;
            CombineTokens(readToken, enumeratorToken);
            return _version;
        }

        public EventEnvelope Current(int version)
        {
            if (version != _version || !_hasCurrent || _rows is not { } rows)
                return default;

            var raw = rows.Current;
            return new EventEnvelope(_id, raw.Position, _event!, raw.Metadata);
        }

        public ValueTask<bool> MoveNextAsync(int version)
            => version == _version && _running ? MoveNextCoreAsync() : new ValueTask<bool>(false);

        public ValueTask DisposeAsync(int version)
        {
            if (version != _version)
                return default;

            var rows = _rows;
            Reset();
            return rows is null ? default : rows.DisposeAsync();
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> MoveNextCoreAsync()
        {
            var store = _store!;
            _hasCurrent = false;
            _event = null;
            try
            {
                _rows ??= OpenPage(store);
                while (true)
                {
                    if (await _rows.MoveNextAsync().ConfigureAwait(false))
                    {
                        var raw = _rows.Current;
                        _rowsInPage++;
                        _from = raw.Position;
                        if (!store.TryDeserialize(raw, out _event))
                            continue; // unknown event type, skipped for forward compatibility

                        _yielded++;
                        _hasCurrent = true;
                        return true;
                    }

                    // The adapter's enumerator is released as soon as its rows run out, as an
                    // await foreach over it would, so a SQL adapter frees its connection before
                    // the caller disposes the enumerator.
                    var page = _rows;
                    _rows = null;
                    await page.DisposeAsync().ConfigureAwait(false);

                    if (!HasNextPage())
                    {
                        _event = null;
                        Finish();
                        return false;
                    }

                    _rows = OpenPage(store);
                }
            }
            catch
            {
                _hasCurrent = false;
                _event = null;
                if (_rows is { } rows)
                {
                    _rows = null;
                    await rows.DisposeAsync().ConfigureAwait(false);
                }

                Finish();
                throw;
            }
        }

        /// <summary>Starts the adapter read for the next page, or for the whole stream.</summary>
        private IAsyncEnumerator<RawEvent> OpenPage(EventStore store)
        {
            _pageStart = _from;
            _rowsInPage = 0;
            if (_maxCount == 0)
                return store._adapter.ReadAsync(_id, _from, _token).GetAsyncEnumerator();

            _requested = _maxCount - _yielded;
            return store._adapter.ReadBatchAsync(_id, _from, _requested, _token).GetAsyncEnumerator();
        }

        /// <summary>
        /// Whether the adapter has to be asked for more rows once the current ones run out. A read
        /// of the whole stream is one adapter read. A bounded read whose page held unknown event
        /// types is short, so the rows after it are read.
        /// </summary>
        private bool HasNextPage()
        {
            // A short page is the end of the stream.
            if (_maxCount == 0 || _yielded == _maxCount || _rowsInPage < _requested)
                return false;

            // The next page starts after the last row of this one; an adapter whose positions do
            // not advance would otherwise be read again from the same place forever.
            if (_from.Value <= _pageStart.Value)
                throw new InvalidOperationException(
                    $"The adapter returned events at or before position {_pageStart.Value} for a read after it, so the read cannot advance.");

            return true;
        }

        private void CombineTokens(CancellationToken readToken, CancellationToken enumeratorToken)
        {
            // The same rules as [EnumeratorCancellation] on a compiler-generated iterator.
            if (!readToken.CanBeCanceled || readToken.Equals(enumeratorToken))
            {
                _token = enumeratorToken.CanBeCanceled ? enumeratorToken : readToken;
            }
            else if (!enumeratorToken.CanBeCanceled)
            {
                _token = readToken;
            }
            else
            {
                _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(readToken, enumeratorToken);
                _token = _linkedSource.Token;
            }
        }

        private void Finish()
        {
            _running = false;
            if (_linkedSource is { } linked)
            {
                _linkedSource = null;
                linked.Dispose();
            }
        }

        /// <summary>Ends the enumeration, invalidates its version and returns the state to the pool.</summary>
        private void Reset()
        {
            Finish();
            _version++;
            _store = null;
            _id = default;
            _from = default;
            _pageStart = default;
            _maxCount = 0;
            _requested = 0;
            _rowsInPage = 0;
            _yielded = 0;
            _token = default;
            _rows = null;
            _event = null;
            _hasCurrent = false;
            Return(this);
        }
    }
}

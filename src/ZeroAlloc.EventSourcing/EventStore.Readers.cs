using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing;

public sealed partial class EventStore
{
    /// <summary>
    /// The enumerable and enumerator behind <see cref="ReadAsync"/> and <see cref="ReadBatchAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A compiler-generated async iterator hoists every local into its class: the current
    /// <see cref="EventEnvelope"/>, the <see cref="RawEvent"/> being read and the iterator's own
    /// promise state, which came to 304 B per call. This reader keeps only the adapter's enumerator
    /// and the deserialized event, and builds <see cref="Current"/> from the adapter's current row.
    /// </para>
    /// <para>
    /// Like a compiler-generated iterator, the object returned by the read is its own enumerator
    /// for the first enumeration on the thread that created it; any other enumeration gets a fresh
    /// copy, so the enumerable can be enumerated again, or concurrently. <see cref="MoveNextAsync"/>
    /// uses <see cref="PoolingAsyncValueTaskMethodBuilder{TResult}"/>, so a step that completes
    /// synchronously allocates nothing and one that has to wait for the adapter borrows a pooled box.
    /// </para>
    /// </remarks>
    private abstract class Reader : IAsyncEnumerable<EventEnvelope>, IAsyncEnumerator<EventEnvelope>
    {
        private const int Created = 0;
        private const int Ready = 1;
        private const int Running = 2;
        private const int Finished = 3;

        private readonly int _creatorThreadId = Environment.CurrentManagedThreadId;
        private readonly CancellationToken _readToken;
        private CancellationTokenSource? _linkedSource;
        private IAsyncEnumerator<RawEvent>? _rows;
        private object? _event;
        private bool _hasCurrent;
        private int _state;

        protected Reader(EventStore store, StreamId id, CancellationToken ct)
        {
            Store = store;
            Id = id;
            _readToken = ct;
        }

        protected EventStore Store { get; }

        protected StreamId Id { get; }

        /// <summary>The token the read was started with, which a second enumeration starts from again.</summary>
        protected CancellationToken ReadToken => _readToken;

        /// <summary>The token the adapter reads with: the read's token, the enumerator's, or both linked.</summary>
        protected CancellationToken Token { get; private set; }

        public EventEnvelope Current
        {
            get
            {
                if (!_hasCurrent || _rows is not { } rows)
                    return default;

                var raw = rows.Current;
                return new EventEnvelope(Id, raw.Position, _event!, raw.Metadata);
            }
        }

        public IAsyncEnumerator<EventEnvelope> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var reader = _state == Created && _creatorThreadId == Environment.CurrentManagedThreadId
                ? this
                : Clone();
            reader._state = Ready;
            reader.CombineTokens(cancellationToken);
            return reader;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<bool> MoveNextAsync()
        {
            if (_state == Ready)
                _state = Running;
            else if (_state != Running)
                return false;

            _hasCurrent = false;
            _event = null;
            try
            {
                _rows ??= OpenPage();
                while (true)
                {
                    if (await _rows.MoveNextAsync().ConfigureAwait(false))
                    {
                        var raw = _rows.Current;
                        OnRow(raw);
                        if (!Store.TryDeserialize(raw, out _event))
                            continue; // unknown event type, skipped for forward compatibility

                        OnYield();
                        _hasCurrent = true;
                        return true;
                    }

                    // The adapter's enumerator is released as soon as its rows run out, as an
                    // await foreach over it would, so a SQL adapter frees its connection before
                    // the caller disposes this reader.
                    var page = _rows;
                    _rows = null;
                    await page.DisposeAsync().ConfigureAwait(false);

                    if (!HasNextPage())
                    {
                        _event = null;
                        Finish();
                        return false;
                    }

                    _rows = OpenPage();
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

        public ValueTask DisposeAsync()
        {
            _hasCurrent = false;
            _event = null;
            Finish();
            if (_rows is not { } rows)
                return default;

            _rows = null;
            return rows.DisposeAsync();
        }

        /// <summary>Starts the adapter read for the next page, or for the whole stream.</summary>
        protected abstract IAsyncEnumerator<RawEvent> OpenPage();

        /// <summary>Called for every row the adapter returns, including skipped ones.</summary>
        protected virtual void OnRow(in RawEvent raw)
        {
        }

        /// <summary>Called for every row handed to the caller.</summary>
        protected virtual void OnYield()
        {
        }

        /// <summary>Whether the adapter has to be asked for more rows once the current ones run out.</summary>
        protected virtual bool HasNextPage() => false;

        /// <summary>A reader with the same arguments, for a second enumeration.</summary>
        protected abstract Reader Clone();

        private void CombineTokens(CancellationToken enumeratorToken)
        {
            // The same rules as [EnumeratorCancellation] on a compiler-generated iterator.
            if (!_readToken.CanBeCanceled || _readToken.Equals(enumeratorToken))
            {
                Token = enumeratorToken.CanBeCanceled ? enumeratorToken : _readToken;
            }
            else if (!enumeratorToken.CanBeCanceled)
            {
                Token = _readToken;
            }
            else
            {
                _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(_readToken, enumeratorToken);
                Token = _linkedSource.Token;
            }
        }

        private void Finish()
        {
            _state = Finished;
            if (_linkedSource is { } linked)
            {
                _linkedSource = null;
                linked.Dispose();
            }
        }
    }

    /// <summary>Reads a whole stream with one adapter read.</summary>
    private sealed class StreamReader : Reader
    {
        private readonly StreamPosition _from;

        public StreamReader(EventStore store, StreamId id, StreamPosition from, CancellationToken ct)
            : base(store, id, ct)
        {
            _from = from;
        }

        protected override IAsyncEnumerator<RawEvent> OpenPage()
            => Store._adapter.ReadAsync(Id, _from, Token).GetAsyncEnumerator();

        protected override Reader Clone() => new StreamReader(Store, Id, _from, ReadToken);
    }

    /// <summary>
    /// Reads at most <c>maxCount</c> events, one bounded adapter read per page. A page whose rows
    /// held unknown event types leaves the batch short, so the rows after it are read.
    /// </summary>
    private sealed class BatchReader : Reader
    {
        private readonly StreamPosition _start;
        private readonly int _maxCount;
        private StreamPosition _from;
        private StreamPosition _pageStart;
        private int _requested;
        private int _rowsInPage;
        private int _yielded;

        public BatchReader(EventStore store, StreamId id, StreamPosition from, int maxCount, CancellationToken ct)
            : base(store, id, ct)
        {
            _start = from;
            _from = from;
            _maxCount = maxCount;
        }

        protected override IAsyncEnumerator<RawEvent> OpenPage()
        {
            _requested = _maxCount - _yielded;
            _pageStart = _from;
            _rowsInPage = 0;
            return Store._adapter.ReadBatchAsync(Id, _from, _requested, Token).GetAsyncEnumerator();
        }

        protected override void OnRow(in RawEvent raw)
        {
            _rowsInPage++;
            _from = raw.Position;
        }

        protected override void OnYield() => _yielded++;

        protected override bool HasNextPage()
        {
            // A short page is the end of the stream. A full page that held unknown types leaves
            // the batch short, so the rows after it are read.
            if (_yielded == _maxCount || _rowsInPage < _requested)
                return false;

            // The next page starts after the last row of this one; an adapter whose positions do
            // not advance would otherwise be read again from the same place forever.
            if (_from.Value <= _pageStart.Value)
                throw new InvalidOperationException(
                    $"The adapter returned events at or before position {_pageStart.Value} for a read after it, so the read cannot advance.");

            return true;
        }

        protected override Reader Clone() => new BatchReader(Store, Id, _start, _maxCount, ReadToken);
    }
}

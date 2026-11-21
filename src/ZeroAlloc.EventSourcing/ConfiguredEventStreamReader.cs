using System.Runtime.CompilerServices;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// An <see cref="EventStreamReader"/> whose awaits resume as configured by
/// <see cref="EventStreamReader.ConfigureAwait"/>, the counterpart of
/// <see cref="ConfiguredCancelableAsyncEnumerable{T}"/>.
/// </summary>
/// <remarks>
/// Set the cancellation token before or after configuring the awaits:
/// <c>reader.WithCancellation(ct).ConfigureAwait(false)</c> and
/// <c>reader.ConfigureAwait(false).WithCancellation(ct)</c> are the same.
/// </remarks>
public readonly struct ConfiguredEventStreamReader
{
    private readonly EventStreamReader _reader;
    private readonly bool _continueOnCapturedContext;

    internal ConfiguredEventStreamReader(EventStreamReader reader, bool continueOnCapturedContext)
    {
        _reader = reader;
        _continueOnCapturedContext = continueOnCapturedContext;
    }

    /// <summary>Sets the token the enumeration is cancelled with.</summary>
    /// <param name="cancellationToken">Cancels the enumeration, together with the token the read was started with.</param>
    /// <returns>The reader, configured.</returns>
    public ConfiguredEventStreamReader WithCancellation(CancellationToken cancellationToken)
        => new(_reader.WithCancellation(cancellationToken), _continueOnCapturedContext);

    /// <summary>Starts an enumeration of the read.</summary>
    /// <returns>An enumerator that <c>await foreach</c> disposes when it is done.</returns>
    public Enumerator GetAsyncEnumerator()
        => new(_reader.GetAsyncEnumerator(), _continueOnCapturedContext);

    /// <summary>The enumerator of a <see cref="ConfiguredEventStreamReader"/>.</summary>
    public readonly struct Enumerator
    {
        private readonly EventStreamReader.Enumerator _inner;
        private readonly bool _continueOnCapturedContext;

        internal Enumerator(EventStreamReader.Enumerator inner, bool continueOnCapturedContext)
        {
            _inner = inner;
            _continueOnCapturedContext = continueOnCapturedContext;
        }

        /// <summary>The event the enumerator is at.</summary>
        public EventEnvelope Current => _inner.Current;

        /// <summary>Advances to the next event.</summary>
        /// <returns>An awaitable that is <see langword="true"/> when the enumerator is at an event.</returns>
        public ConfiguredValueTaskAwaitable<bool> MoveNextAsync()
            => _inner.MoveNextAsync().ConfigureAwait(_continueOnCapturedContext);

        /// <summary>Ends the enumeration and releases what the read holds.</summary>
        /// <returns>An awaitable that completes when the read is released.</returns>
        public ConfiguredValueTaskAwaitable DisposeAsync()
            => _inner.DisposeAsync().ConfigureAwait(_continueOnCapturedContext);
    }
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// Abstract projection that adds the ability to rebuild state from the event store.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReplayableProjection{TReadModel}"/> extends <see cref="Projection{TReadModel}"/>
/// to support rebuilding: clearing the current read model and replaying all events from the
/// event store to reconstruct a fresh state. This is useful for:
/// <list type="bullet">
/// <item><description>Recovering from corrupted or inconsistent projection state</description></item>
/// <item><description>Migrating projection logic while keeping the event stream intact</description></item>
/// <item><description>Periodic consistency checks and state repairs</description></item>
/// </list>
/// </para>
/// <para>
/// Subclasses must implement <see cref="GetProjectionKey"/> to provide a stable identifier
/// for storing projection state in <see cref="IProjectionStore"/>.
/// </para>
/// </remarks>
/// <typeparam name="TReadModel">The read model type. Can be a record, struct, or class.</typeparam>
/// <example>
/// <code>
/// public sealed class OrderProjection : ReplayableProjection&lt;OrderSummary&gt;
/// {
///     private readonly StreamId _streamId;
///
///     // SystemTextJsonSerializer comes from ZeroAlloc.Serialisation.SystemTextJson; AppJsonContext is the
///     // application's JsonSerializerContext, so the rebuild stays NativeAOT-safe
///     public OrderProjection(StreamId streamId)
///         : base(
///             new OrderSummary(string.Empty, 0m, null),
///             new SystemTextJsonSerializer&lt;OrderSummary&gt;(AppJsonContext.Default.OrderSummary))
///     {
///         _streamId = streamId;
///     }
///
///     public override string GetProjectionKey() => $"OrderProjection-{_streamId}";
///
///     protected override OrderSummary Apply(OrderSummary current, EventEnvelope @event)
///     {
///         return @event.Event switch
///         {
///             OrderPlaced e => current with { OrderId = e.OrderId, Amount = e.Amount },
///             OrderShipped e => current with { TrackingCode = e.TrackingCode },
///             _ => current
///         };
///     }
/// }
///
/// // Usage: Rebuild state from events
/// var projection = new OrderProjection(streamId);
/// await projection.RebuildAsync(projectionStore, streamId, eventStore);
/// // projection.Current now contains the full reconstructed state
/// </code>
/// </example>
public abstract class ReplayableProjection<TReadModel> : Projection<TReadModel>
{
    private const string ReflectionSerializationMessage =
        "RebuildAsync saves the read model with reflection-based System.Text.Json, which is not trim- or "
        + "NativeAOT-safe. Use the constructor that takes an ISerializer<TReadModel> instead.";

    private const string ObsoleteReflectionConstructor =
        "This constructor saves the read model with reflection-based System.Text.Json, which is not trim- or "
        + "NativeAOT-safe. Use the constructor that also takes an ISerializer<TReadModel>. "
        + "This constructor will be removed in the next major version.";

    private const string ObsoleteReflectionConstructorId = "ZAES008";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly TReadModel _initialState;
    private readonly Func<TReadModel, string> _serializeState;

    /// <summary>
    /// Initializes the projection with <see cref="Projection{TReadModel}.Current"/> at <c>default(TReadModel)</c>.
    /// <see cref="RebuildAsync"/> resets to <c>default(TReadModel)</c> as well, and saves the read model with
    /// reflection-based System.Text.Json.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is a valid empty state for a struct, but <see langword="null"/> for a record, class or
    /// collection.
    /// </para>
    /// <para>
    /// Obsolete, ZAES008: reflection-based serialization is not trim- or NativeAOT-safe. Use
    /// <see cref="ReplayableProjection{TReadModel}(TReadModel, ISerializer{TReadModel})"/>, passing
    /// <c>default</c> as the initial state to keep this constructor's behaviour.
    /// </para>
    /// </remarks>
    [Obsolete(ObsoleteReflectionConstructor, DiagnosticId = ObsoleteReflectionConstructorId)]
    [RequiresUnreferencedCode(ReflectionSerializationMessage)]
    [RequiresDynamicCode(ReflectionSerializationMessage)]
    protected ReplayableProjection()
        : this(default!)
    {
    }

    /// <summary>
    /// Initializes the projection with <see cref="Projection{TReadModel}.Current"/> at
    /// <paramref name="initialState"/>. <see cref="RebuildAsync"/> resets to the same value before it replays
    /// the stream, and saves the read model with reflection-based System.Text.Json.
    /// </summary>
    /// <remarks>
    /// Obsolete, ZAES008: reflection-based serialization is not trim- or NativeAOT-safe. Use
    /// <see cref="ReplayableProjection{TReadModel}(TReadModel, ISerializer{TReadModel})"/>.
    /// </remarks>
    /// <param name="initialState">
    /// The read model before any event is applied, for example <c>ImmutableDictionary&lt;string, decimal&gt;.Empty</c>.
    /// The same instance is reused on every rebuild, so it should be immutable.
    /// </param>
    [Obsolete(ObsoleteReflectionConstructor, DiagnosticId = ObsoleteReflectionConstructorId)]
    [RequiresUnreferencedCode(ReflectionSerializationMessage)]
    [RequiresDynamicCode(ReflectionSerializationMessage)]
    protected ReplayableProjection(TReadModel initialState)
    {
        _initialState = initialState;
        _serializeState = static state => JsonSerializer.Serialize(state);
        Current = initialState;
    }

    /// <summary>
    /// Initializes the projection with <see cref="Projection{TReadModel}.Current"/> at
    /// <paramref name="initialState"/>. <see cref="RebuildAsync"/> resets to the same value before it replays
    /// the stream, and saves the read model with <paramref name="serializer"/>. Trim- and NativeAOT-safe.
    /// </summary>
    /// <remarks>
    /// <see cref="IProjectionStore"/> stores text, so <paramref name="serializer"/> must write UTF-8 text, for
    /// example ZeroAlloc.Serialisation.SystemTextJson's <c>SystemTextJsonSerializer&lt;T&gt;</c> over a
    /// <c>JsonTypeInfo&lt;T&gt;</c> from a <c>JsonSerializerContext</c>. <see cref="RebuildAsync"/> throws a
    /// <see cref="DecoderFallbackException"/> when the output is not valid UTF-8, as the output of a binary
    /// serializer such as MemoryPack or MessagePack generally is not.
    /// </remarks>
    /// <param name="initialState">
    /// The read model before any event is applied, for example <c>ImmutableDictionary&lt;string, decimal&gt;.Empty</c>.
    /// The same instance is reused on every rebuild, so it should be immutable.
    /// </param>
    /// <param name="serializer">The serializer that writes the rebuilt read model as UTF-8 text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
    protected ReplayableProjection(TReadModel initialState, ISerializer<TReadModel> serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _initialState = initialState;
        _serializeState = state =>
        {
            var buffer = new ArrayBufferWriter<byte>();
            serializer.Serialize(buffer, state);
            return StrictUtf8.GetString(buffer.WrittenSpan);
        };
        Current = initialState;
    }

    /// <summary>
    /// Gets the unique key for this projection used to store/retrieve state from <see cref="IProjectionStore"/>.
    /// </summary>
    /// <remarks>
    /// Must return a stable, consistent identifier. Recommended format: "ClassName-AggregateId" or similar.
    /// </remarks>
    /// <returns>The projection key.</returns>
    public abstract string GetProjectionKey();

    /// <summary>
    /// Rebuilds the projection by clearing state and replaying all events from the event store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method:
    /// <list type="number">
    /// <item><description>Resets <see cref="Projection{TReadModel}.Current"/> to the initial state passed to the constructor, or <c>default(TReadModel)</c></description></item>
    /// <item><description>Replays all events from the event store via the provided eventStore</description></item>
    /// <item><description>Saves the rebuilt state to the projection store, with the <see cref="ISerializer{T}"/> passed to the constructor, or reflection-based System.Text.Json</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The caller is responsible for providing both the event store and projection store instances.
    /// </para>
    /// </remarks>
    /// <param name="store">The projection store where rebuilt state is saved.</param>
    /// <param name="streamId">The stream to replay events from.</param>
    /// <param name="eventStore">The event store containing the events to replay.</param>
    /// <param name="ct">Cancellation token for graceful shutdown (default: <see cref="CancellationToken.None"/>).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    /// <exception cref="DecoderFallbackException">
    /// The <see cref="ISerializer{T}"/> passed to the constructor did not write valid UTF-8 text.
    /// </exception>
    public async ValueTask RebuildAsync(IProjectionStore store, StreamId streamId, IEventStore eventStore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Start again from the initial state
        Current = _initialState;

        // Replay all events from the store
        await foreach (var @event in eventStore.ReadAsync(streamId, StreamPosition.Start, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await HandleAsync(@event, ct).ConfigureAwait(false);
        }

        // Serialize and save the rebuilt state
        var key = GetProjectionKey();
        var serialized = _serializeState(Current);
        await store.SaveAsync(key, serialized, ct).ConfigureAwait(false);
    }
}

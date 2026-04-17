using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.EventSourcing;

/// <summary>Extension methods on <see cref="EventSourcingBuilder"/> for registering event upcasters.</summary>
public static class EventSourcingBuilderUpcasterExtensions
{
    /// <summary>
    /// Registers an inline upcaster that transforms <typeparamref name="TOld"/> events to
    /// <typeparamref name="TNew"/> during <see cref="IEventStore.ReadAsync"/>.
    /// Multiple upcasters are chained automatically: registering v1→v2 and v2→v3
    /// means a stored v1 event yields a v3 on read.
    /// </summary>
    public static EventSourcingBuilder AddUpcaster<TOld, TNew>(
        this EventSourcingBuilder builder,
        Func<TOld, TNew> upcast)
        where TOld : notnull
        where TNew : notnull
    {
        ArgumentNullException.ThrowIfNull(upcast);
        builder.Services.AddSingleton(
            new UpcasterRegistration(
                typeof(TOld),
                typeof(TNew),
                o => upcast((TOld)o)));
        return builder;
    }

    /// <summary>
    /// Registers a class-based upcaster <typeparamref name="TUpcaster"/> that transforms
    /// <typeparamref name="TOld"/> events to <typeparamref name="TNew"/>.
    /// </summary>
    public static EventSourcingBuilder AddUpcaster<TOld, TNew, TUpcaster>(
        this EventSourcingBuilder builder)
        where TOld : notnull
        where TNew : notnull
        where TUpcaster : class, IEventUpcaster<TOld, TNew>
    {
        builder.Services.AddSingleton<IEventUpcaster<TOld, TNew>, TUpcaster>();
        builder.Services.AddSingleton(sp =>
        {
            var upcaster = sp.GetRequiredService<IEventUpcaster<TOld, TNew>>();
            return new UpcasterRegistration(
                typeof(TOld),
                typeof(TNew),
                o => upcaster.Upcast((TOld)o));
        });
        return builder;
    }
}

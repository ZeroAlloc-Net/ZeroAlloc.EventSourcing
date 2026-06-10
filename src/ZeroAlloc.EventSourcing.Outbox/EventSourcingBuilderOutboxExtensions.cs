using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.EventSourcing.Mediator;

namespace ZeroAlloc.EventSourcing.Outbox;

/// <summary>
/// Chainable registration for <see cref="OutboxDispatcher"/> on an
/// <see cref="EventSourcingBuilder"/>. Wires the dispatcher as a singleton
/// <see cref="IHostedService"/> and the <see cref="OutboxOptions"/> as a singleton
/// configured by the optional <c>configure</c> action.
/// </summary>
public static class EventSourcingBuilderOutboxExtensions
{
    /// <summary>
    /// Registers the <see cref="OutboxDispatcher"/> hosted service that polls the global
    /// stream and dispatches <c>INotification</c> events via <see cref="INotificationDispatcher"/>.
    /// </summary>
    /// <param name="builder">The event-sourcing DI builder.</param>
    /// <param name="configure">Optional <see cref="OutboxOptions"/> mutation. Called once at registration time.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// The dispatcher is constructed via a factory delegate so that
    /// <see cref="IDeadLetterStore"/> remains optional — if no implementation is registered
    /// the dispatcher receives <c>null</c> and skips dead-letter writes.
    /// </remarks>
    public static EventSourcingBuilder AddOutbox(
        this EventSourcingBuilder builder,
        Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new OutboxOptions();
        configure?.Invoke(options);

        builder.Services.TryAddSingleton(options);
        builder.Services.AddSingleton<IHostedService>(sp => new OutboxDispatcher(
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<ICheckpointStore>(),
            sp.GetRequiredService<INotificationDispatcher>(),
            sp.GetService<IDeadLetterStore>(),
            sp.GetRequiredService<OutboxOptions>(),
            sp.GetRequiredService<ILogger<OutboxDispatcher>>()));

        return builder;
    }
}

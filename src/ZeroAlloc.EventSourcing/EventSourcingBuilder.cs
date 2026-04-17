using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.EventSourcing;

/// <summary>
/// A builder for configuring ZeroAlloc.EventSourcing services.
/// Returned by <see cref="ServiceCollectionExtensions.AddEventSourcing"/>.
/// </summary>
public sealed class EventSourcingBuilder
{
    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services { get; }

    internal EventSourcingBuilder(IServiceCollection services) => Services = services;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

/// <summary>
/// The registration shapes <see cref="EventSourcingBuilderExtensions.WithTelemetry"/> decorates:
/// the builder method, hand-written descriptors of every kind, value-type ids, scoped and keyed
/// lifetimes. Decoration goes through <see cref="IAggregateRepository.Accept{TResult}"/>, so
/// none of these need a closed generic built at run time.
/// </summary>
public sealed class WithTelemetryRegistrationTests
{
    private const string SourceName = "ZeroAlloc.EventSourcing";

    [Fact]
    public async Task WithTelemetry_GuidKeyedBuilderRepository_IsDecoratedAndRecordsSpans()
    {
        using var listener = new TestActivityListener(SourceName);
        var services = new ServiceCollection();
        services.AddSingleton(EmptyEventStore());
        services.AddEventSourcing()
                .UseAggregateRepository<GuidAggregate, Guid>(
                    static () => new GuidAggregate(),
                    static id => new StreamId($"guid-{id:N}"))
                .WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<GuidAggregate, Guid>>();
        repo.Should().BeOfType<InstrumentedAggregateRepository<GuidAggregate, Guid>>();

        var id = Guid.NewGuid();
        var loaded = await repo.LoadAsync(id);
        loaded.IsSuccess.Should().BeTrue();
        using var aggregate = loaded.Value;
        aggregate.Id.Should().Be(id);
        (await repo.SaveAsync(aggregate, id)).IsSuccess.Should().BeTrue();

        listener.StoppedActivities.Select(a => a.OperationName)
                .Should().Equal("aggregate.load", "aggregate.save");
        listener.StoppedActivities.Should().AllSatisfy(a =>
            a.GetTagItem("aggregate.type").Should().Be(nameof(GuidAggregate)));
    }

    [Fact]
    public async Task WithTelemetry_HandWrittenGuidKeyedRepository_IsDecoratedAndRecordsSpans()
    {
        using var listener = new TestActivityListener(SourceName);
        var inner = new GuidKeyedFakeRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, Guid>>(inner);
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<FakeAggregate, Guid>>();
        repo.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, Guid>>();

        await repo.LoadAsync(Guid.NewGuid());

        inner.Loads.Should().Be(1);
        listener.StoppedActivities.Should().ContainSingle(a =>
            string.Equals(a.OperationName, "aggregate.load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTelemetry_HandWrittenSnapshotCachingRegistration_IsDecorated()
    {
        using var listener = new TestActivityListener(SourceName);
        var services = new ServiceCollection();
        services.AddSingleton(EmptyEventStore());
        services.AddSingleton(Substitute.For<ISnapshotStore<GuidState>>());
        // No builder method registers a snapshot-caching repository: a hand-written descriptor is
        // the documented way, see docs/examples/SnapshotOptimizedLoading.md.
        services.AddScoped<IAggregateRepository<GuidAggregate, Guid>>(sp =>
            new SnapshotCachingRepositoryDecorator<GuidAggregate, Guid, GuidState>(
                new AggregateRepository<GuidAggregate, Guid>(
                    sp.GetRequiredService<IEventStore>(),
                    static () => new GuidAggregate(),
                    static id => new StreamId($"guid-{id:N}")),
                sp.GetRequiredService<ISnapshotStore<GuidState>>(),
                SnapshotLoadingStrategy.IgnoreSnapshot,
                static (_, _, _) => { }));
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAggregateRepository<GuidAggregate, Guid>>();
        repo.Should().BeOfType<InstrumentedAggregateRepository<GuidAggregate, Guid>>();

        var loaded = await repo.LoadAsync(Guid.NewGuid());
        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Dispose();

        listener.StoppedActivities.Should().ContainSingle(a =>
            string.Equals(a.OperationName, "aggregate.load", StringComparison.Ordinal));
    }

    [Fact]
    public void WithTelemetry_ScopedRepository_KeepsItsScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<IAggregateRepository<FakeAggregate, OrderId>>(_ => new OrderIdFakeRepository());
        services.AddEventSourcing().WithTelemetry();

        services.Should().ContainSingle(d => d.ServiceType == typeof(IAggregateRepository<FakeAggregate, OrderId>))
                .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var a1 = first.ServiceProvider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        var a2 = first.ServiceProvider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        var b = second.ServiceProvider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        a1.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
        a1.Should().BeSameAs(a2);
        a1.Should().NotBeSameAs(b);
    }

    [Fact]
    public void WithTelemetry_ImplementationTypeRegistration_IsDecorated()
    {
        var services = new ServiceCollection();
        services.AddTransient<IAggregateRepository<FakeAggregate, OrderId>, OrderIdFakeRepository>();
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        var second = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        first.Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void WithTelemetry_KeyedRepository_IsDecoratedUnderItsKey()
    {
        var inner = new OrderIdFakeRepository();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAggregateRepository<FakeAggregate, OrderId>>("orders", inner);
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        provider.GetService<IAggregateRepository<FakeAggregate, OrderId>>().Should().BeNull();
        provider.GetRequiredKeyedService<IAggregateRepository<FakeAggregate, OrderId>>("orders")
                .Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
    }

    [Fact]
    public async Task WithTelemetry_CalledTwice_DecoratesOnce()
    {
        using var listener = new TestActivityListener(SourceName);
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(new OrderIdFakeRepository());
        services.AddEventSourcing().WithTelemetry().WithTelemetry();

        services.Count(d => d.ServiceType == typeof(IAggregateRepository<FakeAggregate, OrderId>)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        var repo = provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();
        await repo.LoadAsync(new OrderId(Guid.NewGuid()));

        // A nested decorator would record two load spans for one call.
        listener.StoppedActivities.Should().ContainSingle(a =>
            string.Equals(a.OperationName, "aggregate.load", StringComparison.Ordinal));
    }

    [Fact]
    public void WithTelemetry_RepositoryRegisteredAfterTheCall_IsNotDecorated()
    {
        var services = new ServiceCollection();
        services.AddEventSourcing().WithTelemetry();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(new OrderIdFakeRepository());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>()
                .Should().BeOfType<OrderIdFakeRepository>();
    }

    [Fact]
    public void WithTelemetry_RepositoryWhoseAcceptReturnsNull_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(new BrokenAcceptRepository());
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*Accept*");
    }

    [Fact]
    public void WithTelemetry_FactoryReturningNull_ThrowsNamingTheServiceType()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(_ => null!);
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{typeof(IAggregateRepository<FakeAggregate, OrderId>)}*returned null*");
    }

    [Fact]
    public void WithTelemetry_RepositoryForTwoAggregates_IsDecoratedAsTheInterfaceItsAcceptVisits()
    {
        var both = new TwoAggregateRepository();
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(both);
        services.AddSingleton<IAggregateRepository<FakeAggregate, Guid>>(both);
        services.AddEventSourcing().WithTelemetry();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>()
                .Should().BeOfType<InstrumentedAggregateRepository<FakeAggregate, OrderId>>();
        var act = () => provider.GetRequiredService<IAggregateRepository<FakeAggregate, Guid>>();
        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{typeof(IAggregateRepository<FakeAggregate, Guid>)}*visited a different*");
    }

    [Fact]
    public void InstrumentedAggregateRepository_NullInner_Throws()
    {
        var act = () => new InstrumentedAggregateRepository<FakeAggregate, OrderId>(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    private static IEventStore EmptyEventStore()
    {
        var store = Substitute.For<IEventStore>();
        store.ReadAsync(Arg.Any<StreamId>(), Arg.Any<StreamPosition>(), Arg.Any<CancellationToken>())
             .Returns(_ => Empty());
        return store;

        static async IAsyncEnumerable<EventEnvelope> Empty()
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    public struct GuidState : IAggregateState<GuidState>
    {
        public static GuidState Initial => default;
    }

    public sealed class GuidAggregate : Aggregate<Guid, GuidState>
    {
        protected override GuidState ApplyEvent(GuidState state, object @event) => state;
    }

    private sealed class GuidKeyedFakeRepository : IAggregateRepository<FakeAggregate, Guid>
    {
        public int Loads { get; private set; }

        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(Guid id, CancellationToken ct = default)
        {
            Loads++;
            return ValueTask.FromResult(Result<FakeAggregate, StoreError>.Success(new FakeAggregate()));
        }

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class OrderIdFakeRepository : IAggregateRepository<FakeAggregate, OrderId>
    {
        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(OrderId id, CancellationToken ct = default)
            => ValueTask.FromResult(Result<FakeAggregate, StoreError>.Success(new FakeAggregate()));

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, OrderId id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // Serves two aggregate keys, so the default Accept implementations conflict, error CS8705,
    // and the class has to pick one itself.
    private sealed class TwoAggregateRepository
        : IAggregateRepository<FakeAggregate, OrderId>, IAggregateRepository<FakeAggregate, Guid>
    {
        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(OrderId id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, OrderId id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        TResult IAggregateRepository.Accept<TResult>(IAggregateRepositoryVisitor<TResult> visitor)
            => visitor.Visit<FakeAggregate, OrderId>(this);
    }

    // Breaks the documented Accept contract, to check WithTelemetry fails loudly rather than
    // registering null.
    private sealed class BrokenAcceptRepository : IAggregateRepository<FakeAggregate, OrderId>
    {
        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(OrderId id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, OrderId id, CancellationToken ct = default)
            => throw new NotSupportedException();

        TResult IAggregateRepository.Accept<TResult>(IAggregateRepositoryVisitor<TResult> visitor) => default!;
    }
}

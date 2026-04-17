using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IEventStore>());
        return services;
    }

    [Fact]
    public void UseAggregateRepository_RegistersIAggregateRepository()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseAggregateRepository<TestAggregate, TestId>(
                    () => new TestAggregate(),
                    id => new StreamId($"test-{id.Value}"));

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAggregateRepository<TestAggregate, TestId>>()
                .Should().BeOfType<AggregateRepository<TestAggregate, TestId>>();
    }

    [Fact]
    public void UseAggregateRepository_DoesNotOverwriteUserRegistration()
    {
        var services = BaseServices();
        var custom = new StubAggregateRepository();
        services.AddSingleton<IAggregateRepository<TestAggregate, TestId>>(custom);

        services.AddEventSourcing()
                .UseAggregateRepository<TestAggregate, TestId>(
                    () => new TestAggregate(),
                    id => new StreamId($"test-{id.Value}"));

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAggregateRepository<TestAggregate, TestId>>()
                .Should().BeSameAs(custom);
    }

    [Fact]
    public void UseAggregateRepository_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();

        var result = builder.UseAggregateRepository<TestAggregate, TestId>(
            () => new TestAggregate(),
            id => new StreamId($"test-{id.Value}"));

        result.Should().BeSameAs(builder);
    }

    [Fact]
    public void UseAggregateRepository_CanRegisterMultipleAggregateTypes()
    {
        var services = BaseServices();
        services.AddEventSourcing()
                .UseAggregateRepository<TestAggregate, TestId>(
                    () => new TestAggregate(),
                    id => new StreamId($"test-{id.Value}"))
                .UseAggregateRepository<OtherAggregate, OtherId>(
                    () => new OtherAggregate(),
                    id => new StreamId($"other-{id.Value}"));

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAggregateRepository<TestAggregate, TestId>>()
                .Should().BeOfType<AggregateRepository<TestAggregate, TestId>>();
        provider.GetRequiredService<IAggregateRepository<OtherAggregate, OtherId>>()
                .Should().BeOfType<AggregateRepository<OtherAggregate, OtherId>>();
    }

    // --- Stubs ---

    public readonly record struct TestId(string Value);
    public readonly record struct OtherId(int Value);

    public struct TestState : IAggregateState<TestState>
    {
        public static TestState Initial => default;
    }

    public struct OtherState : IAggregateState<OtherState>
    {
        public static OtherState Initial => default;
    }

    public sealed class TestAggregate : Aggregate<TestId, TestState>
    {
        protected override TestState ApplyEvent(TestState state, object @event) => state;
    }

    public sealed class OtherAggregate : Aggregate<OtherId, OtherState>
    {
        protected override OtherState ApplyEvent(OtherState state, object @event) => state;
    }

    // NSubstitute cannot proxy IAggregateRepository<TestAggregate, TestId> when TestAggregate
    // is a class constrained to IAggregate — use a hand-written stub instead.
    private sealed class StubAggregateRepository : IAggregateRepository<TestAggregate, TestId>
    {
        public ValueTask<ZeroAlloc.Results.Result<TestAggregate, StoreError>> LoadAsync(TestId id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<ZeroAlloc.Results.Result<AppendResult, StoreError>> SaveAsync(TestAggregate aggregate, TestId id, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}

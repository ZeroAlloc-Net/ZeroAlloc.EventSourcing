using AwesomeAssertions;
using NSubstitute;
using static ZeroAlloc.EventSourcing.Aggregates.Tests.EventSourcingBuilderExtensionsTests;

namespace ZeroAlloc.EventSourcing.Aggregates.Tests;

public class AggregateRepositoryVisitorTests
{
    [Fact]
    public void Accept_OnAggregateRepository_VisitsWithTheClosedTypeArguments()
    {
        var repository = new AggregateRepository<TestAggregate, TestId>(
            Substitute.For<IEventStore>(),
            () => new TestAggregate(),
            id => new StreamId($"test-{id.Value}"));

        var visit = ((IAggregateRepository)repository).Accept(new RecordingVisitor());

        visit.Aggregate.Should().Be<TestAggregate>();
        visit.Id.Should().Be<TestId>();
        visit.Repository.Should().BeSameAs(repository);
    }

    [Fact]
    public void Accept_OnSnapshotCachingDecorator_VisitsWithTheDecoratorsTypeArguments()
    {
        var inner = new AggregateRepository<OtherAggregate, OtherId>(
            Substitute.For<IEventStore>(),
            () => new OtherAggregate(),
            id => new StreamId($"other-{id.Value}"));
        var decorator = new SnapshotCachingRepositoryDecorator<OtherAggregate, OtherId, OtherState>(
            inner,
            Substitute.For<ISnapshotStore<OtherState>>(),
            SnapshotLoadingStrategy.IgnoreSnapshot,
            static (_, _, _) => { });

        var visit = ((IAggregateRepository)decorator).Accept(new RecordingVisitor());

        visit.Aggregate.Should().Be<OtherAggregate>();
        visit.Id.Should().Be<OtherId>();
        visit.Repository.Should().BeSameAs(decorator);
    }

    [Fact]
    public void Accept_OnAHandWrittenRepository_UsesTheDefaultImplementation()
    {
        IAggregateRepository repository = new HandWrittenRepository();

        var visit = repository.Accept(new RecordingVisitor());

        visit.Aggregate.Should().Be<TestAggregate>();
        visit.Id.Should().Be<Guid>();
        visit.Repository.Should().BeSameAs(repository);
    }

    private sealed record Visit(Type Aggregate, Type Id, object Repository);

    private sealed class RecordingVisitor : IAggregateRepositoryVisitor<Visit>
    {
        public Visit Visit<TAggregate, TId>(IAggregateRepository<TAggregate, TId> repository)
            where TId : struct
            => new(typeof(TAggregate), typeof(TId), repository);
    }

    private sealed class HandWrittenRepository : IAggregateRepository<TestAggregate, Guid>
    {
        public ValueTask<ZeroAlloc.Results.Result<TestAggregate, StoreError>> LoadAsync(Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<ZeroAlloc.Results.Result<AppendResult, StoreError>> SaveAsync(TestAggregate aggregate, Guid id, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

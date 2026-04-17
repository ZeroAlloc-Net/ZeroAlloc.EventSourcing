using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ZeroAlloc.Serialisation;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class EventSourcingBuilderExtensionsTests
{
    private const string FakePgCs = "Host=localhost;Database=test;Username=test;Password=test";
    private const string FakeSsCs =
        "Server=localhost;Database=test;User Id=sa;Password=test;TrustServerCertificate=true";

    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ISerializerDispatcher>());
        return services;
    }

    // ── PostgreSQL checkpoint ────────────────────────────────────────────────

    [Fact]
    public void UsePostgreSqlCheckpointStore_RegistersICheckpointStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlCheckpointStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeOfType<PostgreSqlCheckpointStore>();
    }

    [Fact]
    public void UsePostgreSqlCheckpointStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<ICheckpointStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UsePostgreSqlCheckpointStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlCheckpointStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UsePostgreSqlCheckpointStore(FakePgCs).Should().BeSameAs(builder);
    }

    // ── PostgreSQL snapshot (open generic) ───────────────────────────────────

    [Fact]
    public void UsePostgreSqlSnapshotStore_RegistersOpenGeneric()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlSnapshotStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>()
                .Should().BeOfType<PostgreSqlSnapshotStore<TestState>>();
    }

    [Fact]
    public void UsePostgreSqlSnapshotStore_DoesNotOverwriteUserSnapshotStore()
    {
        var services = BaseServices();
        var custom = new StubSnapshotStore();
        services.AddSingleton<ISnapshotStore<TestState>>(custom);

        services.AddEventSourcing().UsePostgreSqlSnapshotStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlSnapshotStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UsePostgreSqlSnapshotStore(FakePgCs).Should().BeSameAs(builder);
    }

    // ── PostgreSQL dead-letter ───────────────────────────────────────────────

    [Fact]
    public void UsePostgreSqlDeadLetterStore_RegistersIDeadLetterStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlDeadLetterStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeOfType<PostgreSqlDeadLetterStore>();
    }

    [Fact]
    public void UsePostgreSqlDeadLetterStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<IDeadLetterStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UsePostgreSqlDeadLetterStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlDeadLetterStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UsePostgreSqlDeadLetterStore(FakePgCs).Should().BeSameAs(builder);
    }

    // ── PostgreSQL projection ────────────────────────────────────────────────

    [Fact]
    public void UsePostgreSqlProjectionStore_RegistersIProjectionStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlProjectionStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeOfType<PostgreSqlProjectionStore>();
    }

    [Fact]
    public void UsePostgreSqlProjectionStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<IProjectionStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UsePostgreSqlProjectionStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UsePostgreSqlProjectionStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UsePostgreSqlProjectionStore(FakePgCs).Should().BeSameAs(builder);
    }

    // ── SQL Server checkpoint ────────────────────────────────────────────────

    [Fact]
    public void UseSqlServerCheckpointStore_RegistersICheckpointStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerCheckpointStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeOfType<SqlServerCheckpointStore>();
    }

    [Fact]
    public void UseSqlServerCheckpointStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<ICheckpointStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseSqlServerCheckpointStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerCheckpointStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseSqlServerCheckpointStore(FakeSsCs).Should().BeSameAs(builder);
    }

    // ── SQL Server snapshot (closed generic) ─────────────────────────────────

    [Fact]
    public void UseSqlServerSnapshotStore_RegistersClosedGeneric()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerSnapshotStore<TestState>(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>()
                .Should().BeOfType<SqlServerSnapshotStore<TestState>>();
    }

    [Fact]
    public void UseSqlServerSnapshotStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = new StubSnapshotStore();
        services.AddSingleton<ISnapshotStore<TestState>>(custom);

        services.AddEventSourcing().UseSqlServerSnapshotStore<TestState>(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ISnapshotStore<TestState>>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerSnapshotStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseSqlServerSnapshotStore<TestState>(FakeSsCs).Should().BeSameAs(builder);
    }

    // ── SQL Server dead-letter ───────────────────────────────────────────────

    [Fact]
    public void UseSqlServerDeadLetterStore_RegistersIDeadLetterStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerDeadLetterStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeOfType<SqlServerDeadLetterStore>();
    }

    [Fact]
    public void UseSqlServerDeadLetterStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<IDeadLetterStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseSqlServerDeadLetterStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerDeadLetterStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseSqlServerDeadLetterStore(FakeSsCs).Should().BeSameAs(builder);
    }

    // ── SQL Server projection ────────────────────────────────────────────────

    [Fact]
    public void UseSqlServerProjectionStore_RegistersIProjectionStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerProjectionStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeOfType<SqlServerProjectionStore>();
    }

    [Fact]
    public void UseSqlServerProjectionStore_DoesNotOverwrite()
    {
        var services = BaseServices();
        var custom = Substitute.For<IProjectionStore>();
        services.AddSingleton(custom);

        services.AddEventSourcing().UseSqlServerProjectionStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IProjectionStore>().Should().BeSameAs(custom);
    }

    [Fact]
    public void UseSqlServerProjectionStore_ReturnsBuilder_ForChaining()
    {
        var services = BaseServices();
        var builder = services.AddEventSourcing();
        builder.UseSqlServerProjectionStore(FakeSsCs).Should().BeSameAs(builder);
    }

    private struct TestState { }

    /// <summary>
    /// Hand-written stub for ISnapshotStore&lt;TestState&gt;.
    /// NSubstitute/Castle DynamicProxy cannot mock interfaces with struct type parameters.
    /// </summary>
    private sealed class StubSnapshotStore : ISnapshotStore<TestState>
    {
        public ValueTask<(StreamPosition Position, TestState State)?> ReadAsync(StreamId streamId, CancellationToken ct = default)
            => ValueTask.FromResult<(StreamPosition, TestState)?>(null);

        public ValueTask WriteAsync(StreamId streamId, StreamPosition position, TestState state, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}

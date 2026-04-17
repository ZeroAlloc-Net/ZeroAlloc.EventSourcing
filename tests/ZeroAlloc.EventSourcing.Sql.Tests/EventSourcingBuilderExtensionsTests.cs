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

    // ── PostgreSQL dead-letter ───────────────────────────────────────────────

    [Fact]
    public void UsePostgreSqlDeadLetterStore_RegistersIDeadLetterStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UsePostgreSqlDeadLetterStore(FakePgCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeOfType<PostgreSqlDeadLetterStore>();
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

    // ── SQL Server checkpoint ────────────────────────────────────────────────

    [Fact]
    public void UseSqlServerCheckpointStore_RegistersICheckpointStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerCheckpointStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICheckpointStore>().Should().BeOfType<SqlServerCheckpointStore>();
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

    // ── SQL Server dead-letter ───────────────────────────────────────────────

    [Fact]
    public void UseSqlServerDeadLetterStore_RegistersIDeadLetterStore()
    {
        var services = BaseServices();
        services.AddEventSourcing().UseSqlServerDeadLetterStore(FakeSsCs);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IDeadLetterStore>().Should().BeOfType<SqlServerDeadLetterStore>();
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

    private struct TestState { }
}

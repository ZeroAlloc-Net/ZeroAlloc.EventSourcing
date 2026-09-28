using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Checks what each AddPostgreSql*Store health-check overload puts into
/// <see cref="HealthCheckRegistration"/>: name, failure status, tags and health check type.
/// No database is contacted; the registrations are read from the options.
/// </summary>
public sealed class HealthCheckRegistrationTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Database=test";
    private static readonly string[] CustomTags = ["ready", "db"];

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(ConnectionString);

    public void Dispose() => _dataSource.Dispose();

    private static (HealthCheckRegistration Registration, ServiceProvider Provider) Register(
        Action<IHealthChecksBuilder> add)
    {
        var services = new ServiceCollection();
        add(services.AddHealthChecks());
        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().ContainSingle();
        return (registrations.Single(), provider);
    }

    private static void ShouldBeDefault<THealthCheck>(Action<IHealthChecksBuilder> add, string expectedName)
    {
        var (registration, provider) = Register(add);
        using (provider)
        {
            registration.Name.Should().Be(expectedName);
            registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
            registration.Tags.Should().BeEmpty();
            registration.Factory(provider).Should().BeOfType<THealthCheck>();
        }
    }

    private static void ShouldBeCustom<THealthCheck>(Action<IHealthChecksBuilder> add)
    {
        var (registration, provider) = Register(add);
        using (provider)
        {
            registration.Name.Should().Be("custom");
            registration.FailureStatus.Should().Be(HealthStatus.Degraded);
            registration.Tags.Should().BeEquivalentTo(CustomTags);
            registration.Factory(provider).Should().BeOfType<THealthCheck>();
        }
    }

    // ── event store: plain overloads ──────────────────────────────────────────

    [Fact]
    public void EventStoreCheck_ConnectionString_UsesDefaults()
        => ShouldBeDefault<PostgreSqlEventStoreHealthCheck>(
            b => b.AddPostgreSqlEventStore(ConnectionString), "postgresql-event-store");

    [Fact]
    public void EventStoreCheck_DataSource_UsesDefaults()
        => ShouldBeDefault<PostgreSqlEventStoreHealthCheck>(
            b => b.AddPostgreSqlEventStore(_dataSource), "postgresql-event-store");

    // ── event store: options overload ─────────────────────────────────────────

    [Fact]
    public void EventStoreCheck_Options_ConnectionString_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlEventStoreHealthCheck>(b => b.AddPostgreSqlEventStore(o =>
        {
            o.ConnectionString = ConnectionString;
            o.Name = "custom";
            o.FailureStatus = HealthStatus.Degraded;
            o.Tags = CustomTags;
        }));

    [Fact]
    public void EventStoreCheck_Options_DataSource_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlEventStoreHealthCheck>(b => b.AddPostgreSqlEventStore(o =>
        {
            o.DataSource = _dataSource;
            o.Name = "custom";
            o.FailureStatus = HealthStatus.Degraded;
            o.Tags = CustomTags;
        }));

    [Fact]
    public void EventStoreCheck_Options_WithoutName_UsesDefaultName()
        => ShouldBeDefault<PostgreSqlEventStoreHealthCheck>(
            b => b.AddPostgreSqlEventStore(o => o.ConnectionString = ConnectionString),
            "postgresql-event-store");

    // ── event store: obsolete overloads still honour every parameter ──────────

#pragma warning disable ZAES002
    [Fact]
    public void EventStoreCheck_ObsoleteConnectionString_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlEventStoreHealthCheck>(
            b => b.AddPostgreSqlEventStore(ConnectionString, "custom", HealthStatus.Degraded, CustomTags));

    [Fact]
    public void EventStoreCheck_ObsoleteDataSource_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlEventStoreHealthCheck>(
            b => b.AddPostgreSqlEventStore(_dataSource, "custom", HealthStatus.Degraded, CustomTags));

    [Fact]
    public void EventStoreCheck_ObsoleteNamedTagsOnly_KeepsDefaultName()
    {
        var (registration, provider) = Register(b => b.AddPostgreSqlEventStore(ConnectionString, tags: CustomTags));
        using (provider)
        {
            registration.Name.Should().Be("postgresql-event-store");
            registration.Tags.Should().BeEquivalentTo(CustomTags);
        }
    }
#pragma warning restore ZAES002

    // ── checkpoint store: plain overloads ─────────────────────────────────────

    [Fact]
    public void CheckpointStoreCheck_ConnectionString_UsesDefaults()
        => ShouldBeDefault<PostgreSqlCheckpointStoreHealthCheck>(
            b => b.AddPostgreSqlCheckpointStore(ConnectionString), "postgresql-checkpoint-store");

    [Fact]
    public void CheckpointStoreCheck_DataSource_UsesDefaults()
        => ShouldBeDefault<PostgreSqlCheckpointStoreHealthCheck>(
            b => b.AddPostgreSqlCheckpointStore(_dataSource), "postgresql-checkpoint-store");

    // ── checkpoint store: options overload ────────────────────────────────────

    [Fact]
    public void CheckpointStoreCheck_Options_ConnectionString_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlCheckpointStoreHealthCheck>(b => b.AddPostgreSqlCheckpointStore(o =>
        {
            o.ConnectionString = ConnectionString;
            o.Name = "custom";
            o.FailureStatus = HealthStatus.Degraded;
            o.Tags = CustomTags;
        }));

    [Fact]
    public void CheckpointStoreCheck_Options_DataSource_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlCheckpointStoreHealthCheck>(b => b.AddPostgreSqlCheckpointStore(o =>
        {
            o.DataSource = _dataSource;
            o.Name = "custom";
            o.FailureStatus = HealthStatus.Degraded;
            o.Tags = CustomTags;
        }));

    [Fact]
    public void CheckpointStoreCheck_Options_WithoutName_UsesDefaultName()
        => ShouldBeDefault<PostgreSqlCheckpointStoreHealthCheck>(
            b => b.AddPostgreSqlCheckpointStore(o => o.DataSource = _dataSource),
            "postgresql-checkpoint-store");

    // ── checkpoint store: obsolete overloads still honour every parameter ─────

#pragma warning disable ZAES002
    [Fact]
    public void CheckpointStoreCheck_ObsoleteConnectionString_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlCheckpointStoreHealthCheck>(
            b => b.AddPostgreSqlCheckpointStore(ConnectionString, "custom", HealthStatus.Degraded, CustomTags));

    [Fact]
    public void CheckpointStoreCheck_ObsoleteDataSource_AppliesNameFailureStatusAndTags()
        => ShouldBeCustom<PostgreSqlCheckpointStoreHealthCheck>(
            b => b.AddPostgreSqlCheckpointStore(_dataSource, "custom", HealthStatus.Degraded, CustomTags));
#pragma warning restore ZAES002

    // ── options validation ────────────────────────────────────────────────────

    [Fact]
    public void Options_WithNeitherConnectionStringNorDataSource_Throws()
    {
        var builder = new ServiceCollection().AddHealthChecks();

        FluentActions.Invoking(() => builder.AddPostgreSqlEventStore(_ => { }))
            .Should().Throw<ArgumentException>().WithParameterName("configure");
        FluentActions.Invoking(() => builder.AddPostgreSqlCheckpointStore(_ => { }))
            .Should().Throw<ArgumentException>().WithParameterName("configure");
    }

    [Fact]
    public void Options_WithBothConnectionStringAndDataSource_Throws()
    {
        var builder = new ServiceCollection().AddHealthChecks();
        void Both(PostgreSqlHealthCheckOptions o)
        {
            o.ConnectionString = ConnectionString;
            o.DataSource = _dataSource;
        }

        FluentActions.Invoking(() => builder.AddPostgreSqlEventStore(Both))
            .Should().Throw<ArgumentException>().WithParameterName("configure");
        FluentActions.Invoking(() => builder.AddPostgreSqlCheckpointStore(Both))
            .Should().Throw<ArgumentException>().WithParameterName("configure");
    }

    [Fact]
    public void Options_NullConfigure_Throws()
    {
        var builder = new ServiceCollection().AddHealthChecks();

        FluentActions.Invoking(() => builder.AddPostgreSqlEventStore((Action<PostgreSqlHealthCheckOptions>)null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("configure");
        FluentActions.Invoking(() => builder.AddPostgreSqlCheckpointStore((Action<PostgreSqlHealthCheckOptions>)null!))
            .Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }
}

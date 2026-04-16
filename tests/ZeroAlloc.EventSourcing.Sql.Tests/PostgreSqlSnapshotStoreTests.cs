using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests for <see cref="PostgreSqlSnapshotStore{TState}"/> against a real PostgreSQL database.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests{TStore}"/>.
/// </summary>
[Collection("PostgreSQL")]
public sealed class PostgreSqlSnapshotStoreTests : SnapshotStoreContractTests<PostgreSqlSnapshotStore<SnapshotTestState>>
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;

    /// <summary>Test that constructor rejects null dataSource.</summary>
    [Fact]
    public void Constructor_NullDataSource_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<SnapshotTestState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    /// <inheritdoc/>
    protected override async Task<PostgreSqlSnapshotStore<SnapshotTestState>> CreateStoreAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        var store = new PostgreSqlSnapshotStore<SnapshotTestState>(_dataSource, new JsonEventSerializer());
        await store.EnsureSchemaAsync();
        return store;
    }

    /// <inheritdoc/>
    public override async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.StopAsync();
    }

    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event));

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
            => JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(data.Span), targetType)
               ?? throw new InvalidOperationException("Deserialization returned null");
    }
}

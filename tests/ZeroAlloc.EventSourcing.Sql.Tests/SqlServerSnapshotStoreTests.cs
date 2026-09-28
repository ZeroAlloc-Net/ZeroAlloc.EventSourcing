using System.Text.Json;
using AwesomeAssertions;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests for <see cref="SqlServerSnapshotStore{TState}"/> against a real SQL Server database.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests{TStore}"/>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerSnapshotStoreTests(SqlServerContainerFixture fixture) : SnapshotStoreContractTests<SqlServerSnapshotStore<SnapshotTestState>>
{
    private TestDatabase _database = null!;

    /// <summary>Test that constructor rejects null connection string.</summary>
    [Fact]
    public void Constructor_NullConnectionString_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<SnapshotTestState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    /// <summary>Test that constructor rejects empty connection string.</summary>
    [Fact]
    public void Constructor_EmptyConnectionString_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<SnapshotTestState>(string.Empty))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>Test that constructor rejects whitespace-only connection string.</summary>
    [Fact]
    public void Constructor_WhitespaceConnectionString_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<SnapshotTestState>("   "))
            .Should().Throw<ArgumentException>();
    }

    /// <inheritdoc/>
    protected override async Task<SqlServerSnapshotStore<SnapshotTestState>> CreateStoreAsync()
    {
        _database = await fixture.CreateDatabaseAsync();
        var store = new SqlServerSnapshotStore<SnapshotTestState>(_database.GetConnectionString(), new JsonEventSerializer());
        await store.EnsureSchemaAsync();
        return store;
    }

    /// <inheritdoc/>
    public override async Task DisposeAsync()
    {
        await _database.DisposeAsync();
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

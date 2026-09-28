using System.Text.Json;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Testing;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerDeadLetterStoreTests(SqlServerContainerFixture fixture) : DeadLetterStoreContractTests, IAsyncLifetime
{
    private TestDatabase _database = null!;
    private SqlServerDeadLetterStore _store = null!;

    public async Task InitializeAsync()
    {
        _database = await fixture.CreateDatabaseAsync().ConfigureAwait(false);
        _store = new SqlServerDeadLetterStore(_database.GetConnectionString(), new JsonEventSerializer());
        await _store.EnsureSchemaAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync().ConfigureAwait(false);
    }

    protected override IDeadLetterStore CreateStore() => _store;

    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(@event));

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
            => JsonSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(data.Span), targetType)
               ?? throw new InvalidOperationException("Deserialization returned null");
    }
}

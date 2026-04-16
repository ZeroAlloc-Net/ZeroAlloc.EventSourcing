using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="PostgreSqlSnapshotStore{TState}"/> with Testcontainers PostgreSQL.
/// Tests against a real PostgreSQL database instance.
/// </summary>
[Collection("PostgreSQL")]
public sealed class PostgreSqlSnapshotStoreIntegrationTests : IAsyncLifetime
{
    /// <summary>Test aggregate state for integration testing.</summary>
    public struct OrderState
    {
        /// <summary>Order identifier.</summary>
        public string OrderId { get; set; }

        /// <summary>Order amount.</summary>
        public decimal Amount { get; set; }

        /// <summary>Order status.</summary>
        public string Status { get; set; }
    }

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSnapshotStore<OrderState> _store = null!;
    private TestEventSerializer _serializer = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        _serializer = new TestEventSerializer();
        _store = new PostgreSqlSnapshotStore<OrderState>(_dataSource, _serializer);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.StopAsync();
    }

    [Fact]
    public async Task RoundTrip_WriteAndRead_WithRealDatabase()
    {
        var streamId = new StreamId("pg-test-order-1");
        var state = new OrderState { OrderId = "order-123", Amount = 500m, Status = "Confirmed" };
        var position = new StreamPosition(10);

        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("order-123");
        result.Value.State.Amount.Should().Be(500m);
        result.Value.State.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task MultipleWrites_LastWriteWins_WithRealDatabase()
    {
        var streamId = new StreamId("pg-test-upsert-1");

        var state1 = new OrderState { OrderId = "order-456", Amount = 100m, Status = "Pending" };
        await _store.WriteAsync(streamId, new StreamPosition(5), state1);

        var state2 = new OrderState { OrderId = "order-456", Amount = 250m, Status = "Shipped" };
        await _store.WriteAsync(streamId, new StreamPosition(15), state2);

        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(new StreamPosition(15));
        result.Value.State.Amount.Should().Be(250m);
        result.Value.State.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task LargePayload_Handles_WithRealDatabase()
    {
        var streamId = new StreamId("pg-test-large-1");
        var largeStatus = new string('X', 5000);
        var state = new OrderState { OrderId = "large-order", Amount = 1000m, Status = largeStatus };

        await _store.WriteAsync(streamId, new StreamPosition(1), state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.State.Status.Length.Should().Be(5000);
        result.Value.State.Amount.Should().Be(1000m);
    }

    [Fact]
    public async Task MultipleStreams_Isolated_WithRealDatabase()
    {
        var stream1 = new StreamId("pg-test-s1");
        var stream2 = new StreamId("pg-test-s2");

        var state1 = new OrderState { OrderId = "s1-order", Amount = 111m };
        var state2 = new OrderState { OrderId = "s2-order", Amount = 222m };

        await _store.WriteAsync(stream1, new StreamPosition(1), state1);
        await _store.WriteAsync(stream2, new StreamPosition(1), state2);

        var result1 = await _store.ReadAsync(stream1);
        var result2 = await _store.ReadAsync(stream2);

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Value.State.OrderId.Should().Be("s1-order");
        result1.Value.State.Amount.Should().Be(111m);
        result2.Value.State.OrderId.Should().Be("s2-order");
        result2.Value.State.Amount.Should().Be(222m);
    }

    /// <summary>
    /// Simple serializer for test purposes using JSON.
    /// </summary>
    private sealed class TestEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
        {
            var json = JsonSerializer.Serialize(@event);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public object Deserialize(ReadOnlyMemory<byte> data, Type targetType)
        {
            var json = System.Text.Encoding.UTF8.GetString(data.Span);
            return JsonSerializer.Deserialize(json, targetType) ?? throw new InvalidOperationException("Deserialization returned null");
        }
    }
}

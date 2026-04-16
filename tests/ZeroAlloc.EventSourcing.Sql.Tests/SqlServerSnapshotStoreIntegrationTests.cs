using System.Text.Json;
using FluentAssertions;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Integration tests for <see cref="SqlServerSnapshotStore{TState}"/> with Testcontainers SQL Server.
/// Tests against a real SQL Server database instance.
/// </summary>
[Collection("SqlServer")]
public sealed class SqlServerSnapshotStoreIntegrationTests : IAsyncLifetime
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

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private SqlServerSnapshotStore<OrderState> _store = null!;
    private TestEventSerializer _serializer = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _serializer = new TestEventSerializer();
        _store = new SqlServerSnapshotStore<OrderState>(_container.GetConnectionString(), _serializer);
        await _store.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
    }

    [Fact]
    public async Task RoundTrip_WriteAndRead_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-order-1");
        var state = new OrderState { OrderId = "order-789", Amount = 750m, Status = "Confirmed" };
        var position = new StreamPosition(10);

        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.OrderId.Should().Be("order-789");
        result.Value.State.Amount.Should().Be(750m);
        result.Value.State.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task MultipleWrites_LastWriteWins_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-upsert-1");

        var state1 = new OrderState { OrderId = "order-888", Amount = 200m, Status = "Pending" };
        await _store.WriteAsync(streamId, new StreamPosition(5), state1);

        var state2 = new OrderState { OrderId = "order-888", Amount = 350m, Status = "Shipped" };
        await _store.WriteAsync(streamId, new StreamPosition(15), state2);

        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.Position.Should().Be(new StreamPosition(15));
        result.Value.State.Amount.Should().Be(350m);
        result.Value.State.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task LargePayload_Handles_WithRealDatabase()
    {
        var streamId = new StreamId("ss-test-large-1");
        var largeStatus = new string('Y', 5000);
        var state = new OrderState { OrderId = "large-order", Amount = 2000m, Status = largeStatus };

        await _store.WriteAsync(streamId, new StreamPosition(1), state);
        var result = await _store.ReadAsync(streamId);

        result.Should().NotBeNull();
        result.Value.State.Status.Length.Should().Be(5000);
        result.Value.State.Amount.Should().Be(2000m);
    }

    [Fact]
    public async Task MultipleStreams_Isolated_WithRealDatabase()
    {
        var stream1 = new StreamId("ss-test-s1");
        var stream2 = new StreamId("ss-test-s2");

        var state1 = new OrderState { OrderId = "s1-order", Amount = 333m };
        var state2 = new OrderState { OrderId = "s2-order", Amount = 444m };

        await _store.WriteAsync(stream1, new StreamPosition(1), state1);
        await _store.WriteAsync(stream2, new StreamPosition(1), state2);

        var result1 = await _store.ReadAsync(stream1);
        var result2 = await _store.ReadAsync(stream2);

        result1.Should().NotBeNull();
        result2.Should().NotBeNull();
        result1.Value.State.OrderId.Should().Be("s1-order");
        result1.Value.State.Amount.Should().Be(333m);
        result2.Value.State.OrderId.Should().Be("s2-order");
        result2.Value.State.Amount.Should().Be(444m);
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

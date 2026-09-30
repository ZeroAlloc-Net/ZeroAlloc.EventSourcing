using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Examples.GettingStarted;

/// <summary>
/// This example demonstrates the complete workflow:
/// 1. Create an aggregate and save its events
/// 2. Read events back from the store
/// 3. Reconstruct aggregate state by loading it through the repository
/// 4. Modify the loaded aggregate and save again, with optimistic concurrency
///
/// This file is compiled and run by the test suite, so it only uses the public API.
/// </summary>

// Domain model
public readonly record struct ProductId(Guid Value);

public partial struct InventoryState : IAggregateState<InventoryState>
{
    public static InventoryState Initial => default;

    public int QuantityOnHand { get; private set; }
    public int QuantityReserved { get; private set; }

    public readonly int AvailableQuantity => QuantityOnHand - QuantityReserved;

    internal InventoryState Apply(StockReceivedEvent e) =>
        this with { QuantityOnHand = QuantityOnHand + e.Quantity };

    internal InventoryState Apply(StockReservedEvent e) =>
        this with { QuantityReserved = QuantityReserved + e.Quantity };

    internal InventoryState Apply(StockReleasedEvent e) =>
        this with { QuantityReserved = QuantityReserved - e.Quantity };
}

public record StockReceivedEvent(int Quantity);
public record StockReservedEvent(int Quantity);
public record StockReleasedEvent(int Quantity);

// The source generator adds ApplyEvent and InventoryEventTypeRegistry to this partial class.
public sealed partial class Inventory : Aggregate<ProductId, InventoryState>
{
    public void ReceiveStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");

        Raise(new StockReceivedEvent(quantity));
    }

    public void ReserveStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");

        if (State.AvailableQuantity < quantity)
            throw new InvalidOperationException("Insufficient stock to reserve");

        Raise(new StockReservedEvent(quantity));
    }

    public void ReleaseReservedStock(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive");

        if (State.QuantityReserved < quantity)
            throw new InvalidOperationException("Cannot release more than reserved");

        Raise(new StockReleasedEvent(quantity));
    }
}

public static class AppendAndReadExample
{
    public static async Task<InventoryState> RunAsync()
    {
        // Setup: create the event store and a repository for the aggregate
        var eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new InventoryEventTypeRegistry());
        var repository = new AggregateRepository<Inventory, ProductId>(
            eventStore,
            () => new Inventory(),
            id => new StreamId($"product-{id.Value}"));

        var productId = new ProductId(Guid.NewGuid());
        var streamId = new StreamId($"product-{productId.Value}");

        Console.WriteLine("=== Example: Append and Read Events ===\n");

        // PART 1: SAVE EVENTS
        Console.WriteLine("1. Creating aggregate and saving its events...");

        using (var inventory = new Inventory())
        {
            // Execute commands
            inventory.ReceiveStock(100);          // Raise StockReceivedEvent
            inventory.ReserveStock(30);           // Raise StockReservedEvent
            inventory.ReserveStock(20);           // Raise another StockReservedEvent

            Console.WriteLine($"   Current state: {inventory.State.QuantityOnHand} on hand, "
                + $"{inventory.State.QuantityReserved} reserved, "
                + $"{inventory.State.AvailableQuantity} available");

            // Append the uncommitted events to the store, expecting an empty stream
            var saveResult = await repository.SaveAsync(inventory, productId);
            if (saveResult.IsFailure)
            {
                Console.WriteLine($"   Error: {saveResult.Error}\n");
                return inventory.State;
            }

            Console.WriteLine($"   Saved; stream version is now {saveResult.Value.NextExpectedVersion.Value}\n");
        }

        // PART 2: READ EVENTS
        Console.WriteLine("2. Reading events from stream...");

        var readEvents = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            readEvents.Add(envelope);
            Console.WriteLine($"   Position {envelope.Position.Value}: {envelope.Event.GetType().Name}");
        }

        Console.WriteLine($"   Total events read: {readEvents.Count}\n");

        // PART 3: RECONSTRUCT THE AGGREGATE
        Console.WriteLine("3. Reconstructing aggregate from event stream...");

        // LoadAsync creates a fresh Inventory and replays every event in the stream onto it.
        // Version and OriginalVersion end at the position of the last event.
        var loadResult = await repository.LoadAsync(productId);
        using var reconstructed = loadResult.Value;

        Console.WriteLine($"   Reconstructed state:");
        Console.WriteLine($"      - QuantityOnHand: {reconstructed.State.QuantityOnHand}");
        Console.WriteLine($"      - QuantityReserved: {reconstructed.State.QuantityReserved}");
        Console.WriteLine($"      - AvailableQuantity: {reconstructed.State.AvailableQuantity}");
        Console.WriteLine($"      - Version: {reconstructed.Version.Value}\n");

        // PART 4: MODIFY AND SAVE MORE EVENTS
        Console.WriteLine("4. Modifying reconstructed aggregate and saving new events...");

        reconstructed.ReleaseReservedStock(10);  // Release 10 from the 50 reserved
        Console.WriteLine($"   Released 10 units");
        Console.WriteLine($"   New available: {reconstructed.State.AvailableQuantity}");

        // The repository appends at OriginalVersion, the version the aggregate was loaded at.
        // If another writer appended in between, the save fails with a CONFLICT error.
        var saveResult2 = await repository.SaveAsync(reconstructed, productId);

        if (saveResult2.IsSuccess)
        {
            Console.WriteLine($"   Saved; stream version is now {saveResult2.Value.NextExpectedVersion.Value}\n");
        }
        else
        {
            Console.WriteLine($"   Error: {saveResult2.Error}\n");
            return reconstructed.State;
        }

        // PART 5: READ ALL EVENTS (INCLUDING NEW ONES)
        Console.WriteLine("5. Reading all events (initial + new)...");

        var allEvents = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            allEvents.Add(envelope);
            Console.WriteLine($"   Position {envelope.Position.Value}: {envelope.Event.GetType().Name}");
        }

        Console.WriteLine($"   Total events: {allEvents.Count}\n");

        // PART 6: FINAL SUMMARY
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Aggregate: Product {productId.Value}");
        Console.WriteLine($"Total events: {allEvents.Count}");
        Console.WriteLine($"Final state:");
        Console.WriteLine($"  - On hand: {reconstructed.State.QuantityOnHand}");
        Console.WriteLine($"  - Reserved: {reconstructed.State.QuantityReserved}");
        Console.WriteLine($"  - Available: {reconstructed.State.AvailableQuantity}");

        return reconstructed.State;
    }

    // A reflection-based JSON serializer keeps the example short. In an application, use the
    // built-in ZeroAllocEventSerializer that AddEventSourcing() registers; it is AOT-safe.
    private sealed class JsonEventSerializer : IEventSerializer
    {
        public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
            => JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType());

        public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
            => JsonSerializer.Deserialize(payload.Span, eventType)!;
    }
}

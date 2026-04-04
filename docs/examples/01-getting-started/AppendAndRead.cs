using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.GettingStarted;

/// <summary>
/// This example demonstrates the complete workflow:
/// 1. Create an aggregate
/// 2. Append events to the event store
/// 3. Read events back
/// 4. Reconstruct aggregate state by replaying events
/// </summary>

// Domain model
public readonly record struct ProductId(Guid Value);

public partial struct InventoryState : IAggregateState<InventoryState>
{
    public static InventoryState Initial => default;

    public int QuantityOnHand { get; private set; }
    public int QuantityReserved { get; private set; }

    public int AvailableQuantity => QuantityOnHand - QuantityReserved;

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

    protected override InventoryState ApplyEvent(InventoryState state, object @event) => @event switch
    {
        StockReceivedEvent e => state.Apply(e),
        StockReservedEvent e => state.Apply(e),
        StockReleasedEvent e => state.Apply(e),
        _ => state
    };
}

public class AppendAndReadExample
{
    public static async Task Main()
    {
        // Setup: Create event store
        var eventStore = new InMemoryEventStore();
        var productId = new ProductId(Guid.NewGuid());
        var streamId = new StreamId($"product-{productId.Value}");

        Console.WriteLine("=== Example: Append and Read Events ===\n");

        // PART 1: APPEND EVENTS
        Console.WriteLine("1. Creating aggregate and appending events...");

        // Create aggregate
        var inventory = new Inventory();
        inventory.SetId(productId);

        // Execute commands
        inventory.ReceiveStock(100);          // Raise StockReceivedEvent
        inventory.ReserveStock(30);           // Raise StockReservedEvent
        inventory.ReserveStock(20);           // Raise another StockReservedEvent

        Console.WriteLine($"   Current state: {inventory.State.QuantityOnHand} on hand, "
            + $"{inventory.State.QuantityReserved} reserved, "
            + $"{inventory.State.AvailableQuantity} available");

        // Get events
        var events = inventory.DequeueUncommitted();
        Console.WriteLine($"   Raising {events.Count} events");

        // Append to store
        var appendResult = await eventStore.AppendAsync(streamId, events, StreamPosition.Start);

        if (appendResult.IsSuccess)
        {
            Console.WriteLine($"   ✓ Appended events, positions {appendResult.Value.FirstPosition} to {appendResult.Value.LastPosition}\n");
            inventory.AcceptVersion(appendResult.Value.LastPosition);
        }
        else
        {
            Console.WriteLine($"   ✗ Error: {appendResult.Error}\n");
            return;
        }

        // PART 2: READ EVENTS
        Console.WriteLine("2. Reading events from stream...");

        var readEvents = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            readEvents.Add(envelope);
            Console.WriteLine($"   Position {envelope.Position}: {@envelope.Event.GetType().Name}");
        }

        Console.WriteLine($"   Total events read: {readEvents.Count}\n");

        // PART 3: REPLAY EVENTS TO RECONSTRUCT STATE
        Console.WriteLine("3. Reconstructing aggregate from event stream...");

        var reconstructed = new Inventory();
        reconstructed.SetId(productId);

        // Replay each event
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            // ApplyHistoric applies the event to state AND tracks position
            reconstructed.ApplyHistoric(envelope.Event, envelope.Position);
        }

        Console.WriteLine($"   ✓ Reconstructed state:");
        Console.WriteLine($"      - QuantityOnHand: {reconstructed.State.QuantityOnHand}");
        Console.WriteLine($"      - QuantityReserved: {reconstructed.State.QuantityReserved}");
        Console.WriteLine($"      - AvailableQuantity: {reconstructed.State.AvailableQuantity}");
        Console.WriteLine($"      - Version: {reconstructed.Version}\n");

        // PART 4: MODIFY AND APPEND MORE EVENTS
        Console.WriteLine("4. Modifying reconstructed aggregate and appending new events...");

        reconstructed.ReleaseReservedStock(10);  // Release 10 from the 50 reserved
        Console.WriteLine($"   Released 10 units");
        Console.WriteLine($"   New available: {reconstructed.State.AvailableQuantity}");

        var moreEvents = reconstructed.DequeueUncommitted();
        Console.WriteLine($"   Raising {moreEvents.Count} new event(s)");

        // Append with position-based optimistic locking
        // Must use OriginalVersion (version when loaded) to ensure no conflict
        var appendResult2 = await eventStore.AppendAsync(streamId, moreEvents, reconstructed.OriginalVersion);

        if (appendResult2.IsSuccess)
        {
            Console.WriteLine($"   ✓ Appended events, positions {appendResult2.Value.FirstPosition} to {appendResult2.Value.LastPosition}\n");
            reconstructed.AcceptVersion(appendResult2.Value.LastPosition);
        }
        else
        {
            Console.WriteLine($"   ✗ Error: {appendResult2.Error}\n");
            return;
        }

        // PART 5: READ ALL EVENTS (INCLUDING NEW ONES)
        Console.WriteLine("5. Reading all events (initial + new)...");

        var allEvents = new List<EventEnvelope>();
        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            allEvents.Add(envelope);
            Console.WriteLine($"   Position {envelope.Position}: {@envelope.Event.GetType().Name}");
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
    }
}

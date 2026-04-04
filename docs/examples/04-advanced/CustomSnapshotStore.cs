using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.Advanced;

/// <summary>
/// This example demonstrates implementing a custom snapshot store.
///
/// Snapshots are periodic saves of aggregate state.
/// They allow loading large aggregates without replaying their entire history.
///
/// This example shows:
/// 1. Simple in-memory snapshot store
/// 2. JSON serialization of aggregate state
/// 3. Snapshot loading patterns
/// 4. When to take snapshots
/// </summary>

// Aggregate state (must be a struct)
public partial struct AccountState : IAggregateState<AccountState>
{
    public static AccountState Initial => default;

    public decimal Balance { get; private set; }
    public int TransactionCount { get; private set; }
    public DateTime LastTransactionAt { get; private set; }

    internal AccountState Apply(MoneyDepositedEvent e) => this with
    {
        Balance = Balance + e.Amount,
        TransactionCount = TransactionCount + 1,
        LastTransactionAt = DateTime.UtcNow
    };

    internal AccountState Apply(MoneyWithdrawnEvent e) => this with
    {
        Balance = Balance - e.Amount,
        TransactionCount = TransactionCount + 1,
        LastTransactionAt = DateTime.UtcNow
    };

    public override string ToString() =>
        $"Account: ${Balance:F2}, {TransactionCount} transactions";
}

// Events
public record MoneyDepositedEvent(decimal Amount);
public record MoneyWithdrawnEvent(decimal Amount);

// Aggregate
public sealed partial class Account : Aggregate<Guid, AccountState>
{
    public void Deposit(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive");

        Raise(new MoneyDepositedEvent(amount));
    }

    public void Withdraw(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive");

        if (State.Balance < amount)
            throw new InvalidOperationException("Insufficient funds");

        Raise(new MoneyWithdrawnEvent(amount));
    }

    protected override AccountState ApplyEvent(AccountState state, object @event) => @event switch
    {
        MoneyDepositedEvent e => state.Apply(e),
        MoneyWithdrawnEvent e => state.Apply(e),
        _ => state
    };
}

/// <summary>
/// Simple in-memory snapshot store for demonstration.
/// In production, you'd implement with SQL Server, Redis, etc.
/// </summary>
public class InMemorySnapshotStore : ISnapshotStore<AccountState>
{
    // Storage: stream ID -> (position, serialized state)
    private readonly Dictionary<string, (StreamPosition Position, string StateJson)> _snapshots = new();

    public async ValueTask<(StreamPosition, AccountState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        lock (_snapshots)
        {
            if (!_snapshots.TryGetValue(streamId.Value, out var snapshot))
                return null;

            // Deserialize JSON back to state
            var state = JsonSerializer.Deserialize<AccountState>(snapshot.StateJson)!;
            return (snapshot.Position, state);
        }
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        AccountState state,
        CancellationToken ct = default)
    {
        lock (_snapshots)
        {
            // Serialize state to JSON
            var stateJson = JsonSerializer.Serialize(state);

            // Store (overwrite existing)
            _snapshots[streamId.Value] = (position, stateJson);

            Console.WriteLine($"  [Snapshot] Saved {streamId.Value} at position {position}: {state}");
        }
    }

    public void Clear()
    {
        lock (_snapshots)
        {
            _snapshots.Clear();
        }
    }

    public int Count()
    {
        lock (_snapshots)
        {
            return _snapshots.Count;
        }
    }
}

/// <summary>
/// Strategy for deciding when to take snapshots.
/// </summary>
public interface ISnapshotStrategy
{
    bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition lastSnapshotPosition);
}

/// <summary>
/// Snapshot every N events.
/// </summary>
public class CountBasedSnapshotStrategy : ISnapshotStrategy
{
    private readonly int _interval;

    public CountBasedSnapshotStrategy(int interval = 100)
    {
        _interval = interval;
    }

    public bool ShouldSnapshot(StreamPosition currentPosition, StreamPosition lastSnapshotPosition)
    {
        var eventsSinceLastSnapshot = currentPosition.Value - lastSnapshotPosition.Value;
        return eventsSinceLastSnapshot >= _interval;
    }
}

/// <summary>
/// Usage example showing snapshot patterns.
/// </summary>
public class CustomSnapshotStoreExample
{
    public static async Task Main()
    {
        Console.WriteLine("=== Custom Snapshot Store Example ===\n");

        // Create services
        var snapshotStore = new InMemorySnapshotStore();
        var eventStore = new InMemoryEventStore();
        var strategy = new CountBasedSnapshotStrategy(interval: 5);  // Snapshot every 5 events

        var accountId = new Guid("00000000-0000-0000-0000-000000000001");
        var streamId = new StreamId($"account-{accountId}");

        Console.WriteLine("Step 1: Create account and perform transactions\n");

        // Create and modify account
        var account = new Account();
        account.SetId(accountId);

        // Perform 12 transactions
        for (int i = 0; i < 12; i++)
        {
            if (i % 2 == 0)
                account.Deposit(100m);
            else
                account.Withdraw(50m);
        }

        // Append to event store
        var events = account.DequeueUncommitted();
        var appendResult = await eventStore.AppendAsync(streamId, events, StreamPosition.Start);
        account.AcceptVersion(appendResult.Value.LastPosition);

        Console.WriteLine($"Appended {events.Count} events to stream\n");

        Console.WriteLine("Step 2: Save snapshots at strategic points\n");

        // Simulate reading and snapshotting
        var position = StreamPosition.Start - 1;
        var lastSnapshotPosition = StreamPosition.Start - 1;

        var account2 = new Account();
        account2.SetId(accountId);

        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            account2.ApplyHistoric(envelope.Event, envelope.Position);
            position = envelope.Position;

            // Check if we should snapshot
            if (strategy.ShouldSnapshot(position, lastSnapshotPosition))
            {
                await snapshotStore.WriteAsync(streamId, position, account2.State);
                lastSnapshotPosition = position;
            }
        }

        Console.WriteLine($"\nSnapshots created: {snapshotStore.Count()}\n");

        Console.WriteLine("Step 3: Load account using snapshot\n");

        // Load account with snapshot
        var account3 = new Account();
        account3.SetId(accountId);

        var snapshot = await snapshotStore.ReadAsync(streamId);

        if (snapshot.HasValue)
        {
            var (snapshotPosition, snapshotState) = snapshot.Value;
            account3.LoadSnapshot(snapshotState);

            Console.WriteLine($"Loaded from snapshot at position {snapshotPosition}");
            Console.WriteLine($"Account state: {account3.State}\n");

            // Replay remaining events (if any)
            var remainingEvents = 0;
            await foreach (var envelope in eventStore.ReadAsync(streamId, snapshotPosition.Next()))
            {
                account3.ApplyHistoric(envelope.Event, envelope.Position);
                remainingEvents++;
            }

            Console.WriteLine($"Replayed {remainingEvents} additional events");
            Console.WriteLine($"Final state: {account3.State}");
        }

        Console.WriteLine("\n=== Summary ===");
        Console.WriteLine($"Total events: {events.Count}");
        Console.WriteLine($"Snapshots: {snapshotStore.Count()}");
        Console.WriteLine($"Final account: {account3.State}");
    }
}

/// <summary>
/// Advanced example: Snapshot rebuilding.
/// When you change snapshot strategy or state structure,
/// rebuild all snapshots by replaying events.
/// </summary>
public static class SnapshotRebuilder
{
    public static async Task RebuildSnapshotsAsync(
        IEventStore eventStore,
        ISnapshotStore<AccountState> snapshotStore,
        StreamId streamId,
        ISnapshotStrategy strategy)
    {
        Console.WriteLine($"Rebuilding snapshots for {streamId}...");

        var account = new Account();
        var lastSnapshotPosition = StreamPosition.Start - 1;
        var snapshotCount = 0;

        await foreach (var envelope in eventStore.ReadAsync(streamId, StreamPosition.Start))
        {
            account.ApplyHistoric(envelope.Event, envelope.Position);

            // Check snapshot strategy
            if (strategy.ShouldSnapshot(envelope.Position, lastSnapshotPosition))
            {
                await snapshotStore.WriteAsync(streamId, envelope.Position, account.State);
                lastSnapshotPosition = envelope.Position;
                snapshotCount++;
            }
        }

        Console.WriteLine($"Created {snapshotCount} snapshots for {streamId}");
    }

    // Usage:
    // var snapshotStore = new InMemorySnapshotStore();
    // var eventStore = new InMemoryEventStore();
    // var strategy = new CountBasedSnapshotStrategy(interval: 100);
    // var streamId = new StreamId("account-123");
    //
    // await RebuildSnapshotsAsync(eventStore, snapshotStore, streamId, strategy);
}

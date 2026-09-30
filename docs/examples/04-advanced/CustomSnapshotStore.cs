using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.Examples.Advanced;

/// <summary>
/// This example demonstrates implementing a custom snapshot store.
///
/// Snapshots are periodic saves of aggregate state.
/// They allow loading large aggregates without replaying their entire history.
///
/// This example shows:
/// 1. A simple custom ISnapshotStore that stores state as JSON
/// 2. Writing snapshots on save with an ISnapshotPolicy
/// 3. Loading from a snapshot with SnapshotCachingRepositoryDecorator
/// 4. Rebuilding a snapshot from the full event stream
///
/// This file is compiled and run by the test suite, so it only uses the public API.
/// </summary>

// Aggregate state (must be a struct). [JsonInclude] lets System.Text.Json
// write the private setters when the snapshot store deserializes a snapshot.
public partial struct AccountState : IAggregateState<AccountState>
{
    public static AccountState Initial => default;

    [JsonInclude] public decimal Balance { get; private set; }
    [JsonInclude] public int TransactionCount { get; private set; }

    internal AccountState Apply(MoneyDepositedEvent e) => this with
    {
        Balance = Balance + e.Amount,
        TransactionCount = TransactionCount + 1,
    };

    internal AccountState Apply(MoneyWithdrawnEvent e) => this with
    {
        Balance = Balance - e.Amount,
        TransactionCount = TransactionCount + 1,
    };

    public override readonly string ToString() =>
        $"Account: ${Balance:F2}, {TransactionCount} transactions";
}

// Events
public record MoneyDepositedEvent(decimal Amount);
public record MoneyWithdrawnEvent(decimal Amount);

// Aggregate. The source generator adds ApplyEvent and AccountEventTypeRegistry.
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
}

/// <summary>
/// Simple in-memory snapshot store that keeps the latest snapshot per stream as JSON.
/// In production, you'd implement this with SQL Server, Redis, etc., or use one of the
/// SQL snapshot stores the library ships.
/// </summary>
public sealed class JsonAccountSnapshotStore : ISnapshotStore<AccountState>
{
    // Storage: stream ID -> (position, serialized state)
    private readonly Dictionary<string, (StreamPosition Position, string StateJson)> _snapshots = new();
    private readonly object _lock = new();

    public int WriteCount { get; private set; }

    public ValueTask<(StreamPosition Position, AccountState State)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_snapshots.TryGetValue(streamId.Value, out var snapshot))
                return ValueTask.FromResult<(StreamPosition, AccountState)?>(null);

            // Deserialize JSON back to state
            var state = JsonSerializer.Deserialize<AccountState>(snapshot.StateJson);
            return ValueTask.FromResult<(StreamPosition, AccountState)?>((snapshot.Position, state));
        }
    }

    public ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        AccountState state,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Store (overwrite existing): only the latest snapshot is ever read
            _snapshots[streamId.Value] = (position, JsonSerializer.Serialize(state));
            WriteCount++;
        }

        Console.WriteLine($"  [Snapshot] Saved {streamId.Value} at position {position.Value}: {state}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Usage example showing snapshot patterns.
/// </summary>
public static class CustomSnapshotStoreExample
{
    public static async Task<AccountState> RunAsync()
    {
        Console.WriteLine("=== Custom Snapshot Store Example ===\n");

        // Create services
        var snapshotStore = new JsonAccountSnapshotStore();
        var eventStore = new EventStore(
            new InMemoryEventStoreAdapter(),
            new JsonEventSerializer(),
            new AccountEventTypeRegistry());

        static StreamId StreamIdFor(Guid id) => new($"account-{id}");

        var innerRepository = new AggregateRepository<Account, Guid>(
            eventStore,
            () => new Account(),
            StreamIdFor);

        // The decorator writes a snapshot after a save once 5 events have been appended since
        // the last one, and loads from the latest snapshot plus the events after it.
        var repository = new SnapshotCachingRepositoryDecorator<Account, Guid, AccountState>(
            innerRepository: innerRepository,
            snapshotStore: snapshotStore,
            strategy: SnapshotLoadingStrategy.ValidateAndReplay,
            restoreState: (account, state, position) => account.RestoreState(state, position),
            eventStore: eventStore,
            streamIdFactory: StreamIdFor,
            aggregateFactory: () => new Account(),
            snapshotPolicy: SnapshotPolicy.EveryNEvents(5),
            extractState: account => account.State);

        var accountId = new Guid("00000000-0000-0000-0000-000000000001");

        Console.WriteLine("Step 1: Create account and perform transactions\n");

        // Save after every transaction, as a request handler would
        for (int i = 0; i < 12; i++)
        {
            var loaded = await repository.LoadAsync(accountId);
            using var account = loaded.Value;

            if (i % 2 == 0)
                account.Deposit(100m);
            else
                account.Withdraw(50m);

            var saved = await repository.SaveAsync(account, accountId);
            if (saved.IsFailure)
                throw new InvalidOperationException(saved.Error.ToString());
        }

        Console.WriteLine($"\nSnapshots written: {snapshotStore.WriteCount}\n");

        Console.WriteLine("Step 2: Load account using the latest snapshot\n");

        var snapshot = await snapshotStore.ReadAsync(StreamIdFor(accountId));
        Console.WriteLine($"Latest snapshot is at position {snapshot?.Position.Value}");

        // Restores the snapshot state, then replays only the events after it
        var result = await repository.LoadAsync(accountId);
        using var fromSnapshot = result.Value;
        Console.WriteLine($"Loaded account at version {fromSnapshot.Version.Value}: {fromSnapshot.State}");

        Console.WriteLine("\nStep 3: Rebuild the snapshot from the full stream\n");

        await SnapshotRebuilder.RebuildSnapshotAsync(innerRepository, snapshotStore, accountId, StreamIdFor(accountId));

        Console.WriteLine("\n=== Summary ===");
        Console.WriteLine($"Snapshots written: {snapshotStore.WriteCount}");
        Console.WriteLine($"Final account: {fromSnapshot.State}");

        return fromSnapshot.State;
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

/// <summary>
/// Advanced example: snapshot rebuilding.
/// When you change the state structure, old snapshots no longer match it. Rebuild the
/// snapshot by replaying the full stream through the plain repository, which never reads
/// snapshots, and writing the result.
/// </summary>
public static class SnapshotRebuilder
{
    public static async Task RebuildSnapshotAsync(
        IAggregateRepository<Account, Guid> fullReplayRepository,
        ISnapshotStore<AccountState> snapshotStore,
        Guid accountId,
        StreamId streamId,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Rebuilding snapshot for {streamId}...");

        var loaded = await fullReplayRepository.LoadAsync(accountId, ct);
        if (loaded.IsFailure)
            throw new InvalidOperationException(loaded.Error.ToString());

        using var account = loaded.Value;
        if (account.Version == StreamPosition.Start)
        {
            Console.WriteLine($"Stream {streamId} is empty; nothing to snapshot");
            return;
        }

        // Version is the position of the last replayed event
        await snapshotStore.WriteAsync(streamId, account.Version, account.State, ct);
        Console.WriteLine($"Rebuilt snapshot for {streamId} at position {account.Version.Value}");
    }
}

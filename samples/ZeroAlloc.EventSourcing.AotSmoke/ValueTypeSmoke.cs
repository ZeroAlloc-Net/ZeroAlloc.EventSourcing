using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;

namespace ZeroAlloc.EventSourcing.AotSmoke;

/// <summary>
/// Value-type paths under NativeAOT: struct aggregate state through the repository, the
/// snapshot store and the snapshot decorator; struct events and nullable value-type event
/// fields through the event store, the generated dispatch and a struct-to-struct upcaster.
/// Every check asserts the actual values, not just that something came back.
/// </summary>
internal static class ValueTypeSmoke
{
    private static readonly DateTimeOffset FirstValueDate = new(2026, 3, 14, 9, 30, 0, TimeSpan.FromHours(2));

    private static StreamId StreamFor(AccountId id) => new($"account-{id.Value:N}");

    public static async Task<int> RunAsync()
    {
        var services = new ServiceCollection();
        // Registered before AddEventSourcing, whose TryAdd defaults then leave them alone.
        services.AddSingleton<IEventSerializer, AccountEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, AccountRegistryWithLegacy>();
        services.AddEventSourcing()
            .UseInMemoryEventStore()
            .UseInMemorySnapshotStore<AccountState>()
            .AddUpcaster<FundsDepositedV1, FundsDeposited>(static v1 => new FundsDeposited(v1.Amount, null))
            .UseAggregateRepository<Account, AccountId>(static () => new Account(), StreamFor);

        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IEventStore>();
        var repository = provider.GetRequiredService<IAggregateRepository<Account, AccountId>>();
        // Struct TState resolved from the container, the way an application gets its snapshot store.
        var snapshots = provider.GetRequiredService<ISnapshotStore<AccountState>>();

        // This decorator writes the snapshots and loads by full replay. The snapshot loads are
        // checked at the end, through decorators that restore with Aggregate.RestoreState.
        var decorator = new SnapshotCachingRepositoryDecorator<Account, AccountId, AccountState>(
            repository,
            snapshots,
            SnapshotLoadingStrategy.IgnoreSnapshot,
            static (_, _, _) => throw new InvalidOperationException("restoreState must not run under IgnoreSnapshot"),
            store,
            StreamFor,
            static () => new Account(),
            SnapshotPolicy.EveryNEvents(2),
            static account => account.State);

        var id = new AccountId(Guid.NewGuid());
        var streamId = StreamFor(id);

        // No snapshot yet: the Nullable<(StreamPosition, AccountState)> must come back empty.
        var none = await snapshots.ReadAsync(streamId);
        if (none.HasValue)
            return Fail($"snapshot for a new stream should be absent, got position {none.Value.Position.Value}");

        // Save 1: one event, below the every-2 policy, so no snapshot.
        using (var account = new Account())
        {
            account.Open(overdraftLimit: 250.75m, tier: 3);
            var saved = await decorator.SaveAsync(account, id);
            if (!saved.IsSuccess) return Fail($"first save failed: {saved.Error}");
            if (saved.Value.NextExpectedVersion.Value != 1)
                return Fail($"first save version expected 1, got {saved.Value.NextExpectedVersion.Value}");
        }

        var afterFirst = await snapshots.ReadAsync(streamId);
        if (afterFirst.HasValue)
            return Fail("EveryNEvents(2) should not snapshot at version 1");

        // Save 2: load, then two more events, reaching version 3 and triggering a snapshot.
        var loaded = await decorator.LoadAsync(id);
        if (!loaded.IsSuccess) return Fail($"load after first save failed: {loaded.Error}");
        using (var account = loaded.Value)
        {
            var s = account.State;
            if (!s.IsOpen || s.OverdraftLimit != 250.75m || s.Tier != 3 || account.Version.Value != 1)
                return Fail($"state after replay of AccountOpened is wrong: {s}, version {account.Version.Value}");
            if (account.Id != id)
                return Fail($"loaded account Id expected {id}, got {account.Id}");

            account.Deposit(100.10m, FirstValueDate);
            account.ChangeOverdraftLimit(null);
            var saved = await decorator.SaveAsync(account, id);
            if (!saved.IsSuccess) return Fail($"second save failed: {saved.Error}");
        }

        var expectedAtThree = new AccountState
        {
            IsOpen = true,
            Balance = 100.10m,
            Deposits = 1,
            OverdraftLimit = null,
            Tier = 3,
            LastValueDate = FirstValueDate,
        };

        var snapshot = await snapshots.ReadAsync(streamId);
        if (!snapshot.HasValue)
            return Fail("EveryNEvents(2) should have snapshotted at version 3");
        if (snapshot.Value.Position.Value != 3)
            return Fail($"snapshot position expected 3, got {snapshot.Value.Position.Value}");
        if (snapshot.Value.State != expectedAtThree)
            return Fail($"snapshot state expected {expectedAtThree}, got {snapshot.Value.State}");
        if (snapshot.Value.State.LastValueDate is not { } snapDate || snapDate.Offset != TimeSpan.FromHours(2))
            return Fail($"snapshot LastValueDate lost its value or offset: {snapshot.Value.State.LastValueDate}");

        // A retired struct event written straight to the stream, as an old producer would have.
        var legacy = await store.AppendAsync(
            streamId,
            new object[] { new FundsDepositedV1(9.90m) }.AsMemory(),
            new StreamPosition(3));
        if (!legacy.IsSuccess) return Fail($"append of FundsDepositedV1 failed: {legacy.Error}");

        // Save 3: one more event, version 5. 5 - 3 >= 2 so the snapshot moves to 5.
        loaded = await decorator.LoadAsync(id);
        if (!loaded.IsSuccess) return Fail($"load after legacy append failed: {loaded.Error}");
        using (var account = loaded.Value)
        {
            var s = account.State;
            // The upcaster turned FundsDepositedV1 into FundsDeposited with a null ValueDate.
            if (s.Deposits != 2 || s.Balance != 110.00m || s.LastValueDate is not null || account.Version.Value != 4)
                return Fail($"state after upcast replay is wrong: {s}, version {account.Version.Value}");

            account.ChangeOverdraftLimit(-0.01m);
            var saved = await decorator.SaveAsync(account, id);
            if (!saved.IsSuccess) return Fail($"third save failed: {saved.Error}");
        }

        var expectedAtFive = new AccountState
        {
            IsOpen = true,
            Balance = 110.00m,
            Deposits = 2,
            OverdraftLimit = -0.01m,
            Tier = 3,
            LastValueDate = null,
        };

        snapshot = await snapshots.ReadAsync(streamId);
        if (!snapshot.HasValue || snapshot.Value.Position.Value != 5 || snapshot.Value.State != expectedAtFive)
            return Fail($"snapshot at 5 expected {expectedAtFive}, got {(snapshot.HasValue ? snapshot.Value.State.ToString() : "none")}");

        // The events themselves, read back through the store: struct boxes with nullable fields.
        var index = 0;
        await foreach (var envelope in store.ReadAsync(streamId, StreamPosition.Start))
        {
            index++;
            var ok = (index, envelope.Event) switch
            {
                (1, AccountOpened e) => e.OverdraftLimit == 250.75m && e.Tier == 3,
                (2, FundsDeposited e) => e.Amount == 100.10m && e.ValueDate == FirstValueDate,
                (3, OverdraftLimitChanged e) => e.NewLimit is null,
                (4, FundsDeposited e) => e.Amount == 9.90m && e.ValueDate is null,
                (5, OverdraftLimitChanged e) => e.NewLimit == -0.01m,
                _ => false,
            };
            if (!ok || envelope.Position.Value != index)
                return Fail($"event {index} at position {envelope.Position.Value} is wrong: {envelope.Event}");
        }
        if (index != 5) return Fail($"expected 5 events in the stream, read {index}");

        // Save 4 through the IgnoreSnapshot decorator: version 6, one event past the snapshot at 5,
        // below the every-2 policy, so the snapshot stays at 5 and the next loads have a tail to replay.
        loaded = await decorator.LoadAsync(id);
        if (!loaded.IsSuccess) return Fail($"load before the tail event failed: {loaded.Error}");
        using (var account = loaded.Value)
        {
            account.Deposit(0.05m, FirstValueDate);
            var saved = await decorator.SaveAsync(account, id);
            if (!saved.IsSuccess || saved.Value.NextExpectedVersion.Value != 6)
                return Fail($"tail save expected version 6, got {(saved.IsSuccess ? saved.Value.NextExpectedVersion.Value.ToString() : saved.Error.ToString())}");
        }

        // Snapshot loads: the struct state at 5 restored through the public RestoreState, then the
        // struct event at 6 replayed on top. Both strategies that read the snapshot.
        var expectedAtSix = expectedAtFive with { Balance = 110.05m, Deposits = 3, LastValueDate = FirstValueDate };
        var check = await LoadFromSnapshotAsync(
            SnapshotLoadingStrategy.ValidateAndReplay, repository, snapshots, store, id, expectedAtFive, expectedAtSix, appendAfterLoad: false);
        if (check != 0) return check;
        check = await LoadFromSnapshotAsync(
            SnapshotLoadingStrategy.TrustSnapshot, repository, snapshots, store, id, expectedAtFive, expectedAtSix, appendAfterLoad: true);
        if (check != 0) return check;

        Console.WriteLine("AOT smoke: value types PASS");
        return 0;
    }

    private static async Task<int> LoadFromSnapshotAsync(
        SnapshotLoadingStrategy strategy,
        IAggregateRepository<Account, AccountId> repository,
        ISnapshotStore<AccountState> snapshots,
        IEventStore store,
        AccountId id,
        AccountState expectedSnapshot,
        AccountState expectedLoaded,
        bool appendAfterLoad)
    {
        var restores = 0;
        StreamPosition restoredAt = default;
        AccountState restoredState = default;
        var decorator = new SnapshotCachingRepositoryDecorator<Account, AccountId, AccountState>(
            repository,
            snapshots,
            strategy,
            (account, state, pos) =>
            {
                restores++;
                restoredAt = pos;
                restoredState = state;
                account.RestoreState(state, pos);
            },
            store,
            StreamFor,
            static () => new Account());

        var loaded = await decorator.LoadAsync(id);
        if (!loaded.IsSuccess) return Fail($"{strategy} load failed: {loaded.Error}");
        using var account = loaded.Value;

        if (restores != 1 || restoredAt.Value != 5 || restoredState != expectedSnapshot)
            return Fail($"{strategy} should restore the snapshot at 5 once, restored {restores}x at {restoredAt.Value}: {restoredState}");
        if (account.State != expectedLoaded)
            return Fail($"{strategy} state after snapshot plus tail expected {expectedLoaded}, got {account.State}");
        if (account.State.LastValueDate is not { } date || date.Offset != TimeSpan.FromHours(2))
            return Fail($"{strategy} LastValueDate lost its value or offset: {account.State.LastValueDate}");
        if (account.Version.Value != 6 || account.OriginalVersion.Value != 6)
            return Fail($"{strategy} versions expected 6/6, got {account.Version.Value}/{account.OriginalVersion.Value}");
        if (account.Id != id)
            return Fail($"{strategy} loaded account Id expected {id}, got {account.Id}");

        // Restoring over events that are already applied is refused.
        try
        {
            account.RestoreState(AccountState.Initial, new StreamPosition(1));
            return Fail($"{strategy} RestoreState on a loaded aggregate should have thrown");
        }
        catch (InvalidOperationException)
        {
            // expected
        }
        if (account.State != expectedLoaded || account.Version.Value != 6)
            return Fail($"{strategy} a refused RestoreState changed the aggregate: {account.State}, version {account.Version.Value}");

        if (!appendAfterLoad) return 0;

        // The restored OriginalVersion is the expected version of the next append.
        account.ChangeOverdraftLimit(1000m);
        var saved = await decorator.SaveAsync(account, id);
        if (!saved.IsSuccess) return Fail($"{strategy} save after snapshot load failed: {saved.Error}");
        if (saved.Value.NextExpectedVersion.Value != 7)
            return Fail($"{strategy} save after snapshot load expected version 7, got {saved.Value.NextExpectedVersion.Value}");

        var full = await repository.LoadAsync(id);
        if (!full.IsSuccess) return Fail($"full replay after snapshot-load save failed: {full.Error}");
        using var replayed = full.Value;
        var expectedAtSeven = expectedLoaded with { OverdraftLimit = 1000m };
        if (replayed.State != expectedAtSeven || replayed.Version.Value != 7)
            return Fail($"full replay after snapshot-load save expected {expectedAtSeven} at 7, got {replayed.State} at {replayed.Version.Value}");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AOT smoke: FAIL (value types) - {message}");
        return 1;
    }
}

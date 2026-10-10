using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.EventSourcing.Telemetry;

namespace ZeroAlloc.EventSourcing.AotSmoke;

/// <summary>
/// WithTelemetry() under NativeAOT with a plain <see cref="Guid"/> as TId. It decorates through
/// IAggregateRepository.Accept, so the closed InstrumentedAggregateRepository is built from static
/// types. Covers the builder registration and a hand-written scoped snapshot-caching registration,
/// and checks the decorator records spans and counters on a save and load round trip.
/// </summary>
internal static class TelemetrySmoke
{
    private const string SourceName = "ZeroAlloc.EventSourcing";

    private static StreamId StreamFor(Guid id) => new($"guid-account-{id:N}");

    public static async Task<int> RunAsync()
    {
        var activities = new List<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, SourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity.OperationName),
        };
        ActivitySource.AddActivityListener(activityListener);

        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        using var meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, SourceName, StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            counters[instrument.Name] = counters.GetValueOrDefault(instrument.Name) + value);
        meterListener.Start();

        var services = new ServiceCollection();
        services.AddSingleton<IEventSerializer, AccountEventSerializer>();
        services.AddSingleton<IEventTypeRegistry, AccountRegistryWithLegacy>();
        // Hand-written and scoped: no builder method registers a snapshot-caching repository.
        services.AddScoped<IAggregateRepository<SnapshotAccount, Guid>>(sp =>
            new SnapshotCachingRepositoryDecorator<SnapshotAccount, Guid, AccountState>(
                new AggregateRepository<SnapshotAccount, Guid>(
                    sp.GetRequiredService<IEventStore>(), static () => new SnapshotAccount(), StreamFor),
                sp.GetRequiredService<ISnapshotStore<AccountState>>(),
                SnapshotLoadingStrategy.IgnoreSnapshot,
                static (_, _, _) => throw new InvalidOperationException("restoreState must not run under IgnoreSnapshot")));
        services.AddEventSourcing()
            .UseInMemoryEventStore()
            .UseInMemorySnapshotStore<AccountState>()
            .UseAggregateRepository<Account, Guid>(static () => new Account(), StreamFor)
            .WithTelemetry()
            .WithTelemetry();

        await using var provider = services.BuildServiceProvider();

        var repository = provider.GetRequiredService<IAggregateRepository<Account, Guid>>();
        if (repository is not InstrumentedAggregateRepository<Account, Guid>)
            return Fail($"builder repository should be instrumented, got {repository.GetType().Name}");

        var id = Guid.NewGuid();
        using (var account = new Account())
        {
            account.Open(overdraftLimit: 10m, tier: 1);
            account.Deposit(42.50m, valueDate: null);
            var saved = await repository.SaveAsync(account, id);
            if (!saved.IsSuccess) return Fail($"save failed: {saved.Error}");
            if (saved.Value.NextExpectedVersion.Value != 2)
                return Fail($"save version expected 2, got {saved.Value.NextExpectedVersion.Value}");
        }

        var loaded = await repository.LoadAsync(id);
        if (!loaded.IsSuccess) return Fail($"load failed: {loaded.Error}");
        using (var account = loaded.Value)
        {
            if (!account.State.IsOpen || account.State.Balance != 42.50m || account.Version.Value != 2)
                return Fail($"loaded state is wrong: {account.State}, version {account.Version.Value}");
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var scoped = scope.ServiceProvider.GetRequiredService<IAggregateRepository<SnapshotAccount, Guid>>();
            if (scoped is not InstrumentedAggregateRepository<SnapshotAccount, Guid>)
                return Fail($"hand-written snapshot repository should be instrumented, got {scoped.GetType().Name}");
            if (!ReferenceEquals(scoped, scope.ServiceProvider.GetRequiredService<IAggregateRepository<SnapshotAccount, Guid>>()))
                return Fail("the scoped repository should resolve to one instance per scope");

            var snapshotLoad = await scoped.LoadAsync(Guid.NewGuid());
            if (!snapshotLoad.IsSuccess) return Fail($"snapshot repository load failed: {snapshotLoad.Error}");
            snapshotLoad.Value.Dispose();
        }

        meterListener.RecordObservableInstruments();

        // WithTelemetry() was called twice: a nested decorator would double every span.
        string[] expected = ["aggregate.save", "aggregate.load", "aggregate.load"];
        if (!activities.SequenceEqual(expected, StringComparer.Ordinal))
            return Fail($"spans expected [{string.Join(", ", expected)}], got [{string.Join(", ", activities)}]");
        var saves = counters.GetValueOrDefault("aggregate.saves_total");
        var loads = counters.GetValueOrDefault("aggregate.loads_total");
        if (saves != 1 || loads != 2)
            return Fail($"counters expected 1 save and 2 loads, got {saves} and {loads}");

        Console.WriteLine("AOT smoke: telemetry PASS");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AOT smoke: FAIL (telemetry) - {message}");
        return 1;
    }
}

/// <summary>A second aggregate over the Account state, so the snapshot registration is its own closed type.</summary>
public sealed partial class SnapshotAccount : Aggregate<AccountId, AccountState>
{
}

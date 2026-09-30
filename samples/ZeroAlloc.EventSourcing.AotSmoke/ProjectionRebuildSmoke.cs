using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing.InMemory;
using ZeroAlloc.Serialisation.SystemTextJson;

namespace ZeroAlloc.EventSourcing.AotSmoke;

#pragma warning disable MA0048 // co-located types for a compact sample

/// <summary>A record read model, so the rebuild starts from a non-null initial state.</summary>
public sealed record DepositTotals(int Deposits, decimal Balance)
{
    public static DepositTotals Empty { get; } = new(0, 0m);
}

[JsonSerializable(typeof(DepositTotals))]
internal sealed partial class ProjectionJsonContext : JsonSerializerContext;

/// <summary>
/// Rebuilds through the ISerializer constructor, which is the AOT-safe path; the constructors
/// without a serializer carry RequiresUnreferencedCode and RequiresDynamicCode.
/// </summary>
internal sealed class DepositTotalsProjection : ReplayableProjection<DepositTotals>
{
    public DepositTotalsProjection()
        : base(DepositTotals.Empty, new SystemTextJsonSerializer<DepositTotals>(ProjectionJsonContext.Default.DepositTotals))
    {
    }

    public override string GetProjectionKey() => "deposit-totals";

    protected override DepositTotals Apply(DepositTotals current, EventEnvelope @event) => @event.Event switch
    {
        FundsDeposited e => current with { Deposits = current.Deposits + 1, Balance = current.Balance + e.Amount },
        _ => current
    };
}

/// <summary>ReplayableProjection.RebuildAsync with an ISerializer under NativeAOT, issue #415.</summary>
internal static class ProjectionRebuildSmoke
{
    public static async Task<int> RunAsync()
    {
        var eventStore = new EventStore(new InMemoryEventStoreAdapter(), new AccountEventSerializer(), new AccountRegistryWithLegacy());
        var projectionStore = new InMemoryProjectionStore();
        var streamId = new StreamId("rebuild-smoke");
        await eventStore.AppendAsync(
            streamId,
            new object[] { new AccountOpened(null, null), new FundsDeposited(10.5m, null), new FundsDeposited(4m, null) },
            StreamPosition.Start);

        var projection = new DepositTotalsProjection();
        if (!ReferenceEquals(projection.Current, DepositTotals.Empty))
            return Fail("a new projection should start at the initial state");

        await projection.RebuildAsync(projectionStore, streamId, eventStore);
        // A second rebuild must start from the initial state again, not add to the first.
        await projection.RebuildAsync(projectionStore, streamId, eventStore);

        var expected = new DepositTotals(2, 14.5m);
        if (projection.Current != expected)
            return Fail($"rebuilt state expected {expected}, got {projection.Current}");

        var saved = await projectionStore.LoadAsync("deposit-totals");
        if (saved is null)
            return Fail("the rebuilt state was not saved");
        var restored = JsonSerializer.Deserialize(saved, ProjectionJsonContext.Default.DepositTotals);
        if (restored != expected)
            return Fail($"saved state expected {expected}, got {restored} from {saved}");

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AOT smoke: FAIL (projection rebuild) - {message}");
        return 1;
    }
}

#pragma warning restore MA0048

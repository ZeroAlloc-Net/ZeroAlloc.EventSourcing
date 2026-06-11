using System;
using System.Threading.Tasks;
using FluentAssertions;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

/// <summary>
/// Documentation-via-test for the aggregate-version-based idempotency recipe
/// described in the README. The Outbox dispatches with at-least-once semantics,
/// so handlers MUST be idempotent. This test demonstrates the canonical pattern:
/// the second AppendAsync attempt at the same expectedVersion fails with
/// <c>StoreError.Conflict</c>, which the handler can swallow safely.
/// </summary>
public class IdempotencyDemoTests
{
    [Fact]
    public async Task Aggregate_version_check_makes_redelivery_a_no_op()
    {
        // Reuse the shared TestHarness wiring (the EventStore needs serializer+registry).
        var (store, _, _) = TestHarness.New();

        // First call: handler appends TestEventA at expectedVersion = Start.
        var append1 = await store.AppendAsync(
            new StreamId("acc-1"),
            new object[] { new TestEventA(10) }.AsMemory(),
            StreamPosition.Start);
        append1.IsSuccess.Should().BeTrue();

        // Simulated redelivery: handler tries to apply the same operation at the same
        // expectedVersion. The aggregate's version has already advanced past Start,
        // so AppendAsync returns a CONFLICT StoreError — handler swallows + considers
        // the work already done.
        var append2 = await store.AppendAsync(
            new StreamId("acc-1"),
            new object[] { new TestEventA(10) }.AsMemory(),
            StreamPosition.Start);
        append2.IsSuccess.Should().BeFalse();
        // The "CONFLICT" literal here mirrors the code emitted by StoreError.Conflict(...).
        // Stringly-typed for v0.1 because StoreError has no public ConflictCode constant.
        // TODO(v0.2): expose StoreError.ConflictCode as a public const and replace this
        // magic string. See https://github.com/zeroalloc-net/zeroalloc.eventsourcing/issues
        // (issue TBD — file when the v0.1 ships).
        append2.Error.Code.Should().Be("CONFLICT");
    }
}

// MeterListener attaches process-wide to any Meter named "ZeroAlloc.EventSourcing".
// xUnit's default per-class parallelism causes sibling tests to record measurements
// into each other's listeners, breaking ContainSingle assertions on counters.
// See InstrumentedAggregateRepositoryTests.SaveAsync_IncrementsCounter_OnSuccess.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

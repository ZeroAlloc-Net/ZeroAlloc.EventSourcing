using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Telemetry.Tests;

/// <summary>
/// End-to-end tests verifying that <see cref="EventSourcingBuilderExtensions.WithTelemetry"/>
/// produces working telemetry on the resolved <see cref="IAggregateRepository{TAggregate, TId}"/>.
/// </summary>
public sealed class AggregateTelemetryTests
{
    private const string SourceName = "ZeroAlloc.EventSourcing";
    private const string MeterName = "ZeroAlloc.EventSourcing";

    private static readonly OrderId _orderId = new(Guid.NewGuid());
    private static readonly StreamId _streamId = new("test-stream");

    private static IServiceProvider BuildProviderWithFake(FakeAggregateRepository fake)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAggregateRepository<FakeAggregate, OrderId>>(fake);
        services.AddEventSourcing().WithTelemetry();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task WithTelemetry_LoadAsync_StartsAggregateLoadActivity()
    {
        using var listener = new TestActivityListener(SourceName);
        var fake = new FakeAggregateRepository();
        using var sp = (ServiceProvider)BuildProviderWithFake(fake);

        var repo = sp.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        await repo.LoadAsync(_orderId);

        listener.StoppedActivities
            .Should().ContainSingle(a => string.Equals(a.OperationName, "aggregate.load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTelemetry_SaveAsync_StartsAggregateSaveActivity()
    {
        using var listener = new TestActivityListener(SourceName);
        var fake = new FakeAggregateRepository();
        using var sp = (ServiceProvider)BuildProviderWithFake(fake);

        var repo = sp.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        await repo.SaveAsync(new FakeAggregate(), _orderId);

        listener.StoppedActivities
            .Should().ContainSingle(a => string.Equals(a.OperationName, "aggregate.save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTelemetry_LoadAsync_RecordsLoadsTotalCounter()
    {
        var measurements = new ConcurrentBag<(string Name, long Value)>();
        using var meterListener = CreateLongMeterListener(measurements);

        var fake = new FakeAggregateRepository();
        using var sp = (ServiceProvider)BuildProviderWithFake(fake);
        var repo = sp.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        await repo.LoadAsync(_orderId);

        meterListener.RecordObservableInstruments();
        measurements.Where(m => string.Equals(m.Name, "aggregate.loads_total", StringComparison.Ordinal))
                    .Sum(m => m.Value)
                    .Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task WithTelemetry_SaveAsync_RecordsSavesTotalCounter()
    {
        var measurements = new ConcurrentBag<(string Name, long Value)>();
        using var meterListener = CreateLongMeterListener(measurements);

        var fake = new FakeAggregateRepository();
        using var sp = (ServiceProvider)BuildProviderWithFake(fake);
        var repo = sp.GetRequiredService<IAggregateRepository<FakeAggregate, OrderId>>();

        await repo.SaveAsync(new FakeAggregate(), _orderId);

        meterListener.RecordObservableInstruments();
        measurements.Where(m => string.Equals(m.Name, "aggregate.saves_total", StringComparison.Ordinal))
                    .Sum(m => m.Value)
                    .Should().BeGreaterThanOrEqualTo(1);
    }

    private static MeterListener CreateLongMeterListener(ConcurrentBag<(string Name, long Value)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (string.Equals(instrument.Meter.Name, MeterName, StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            sink.Add((instrument.Name, measurement)));
        listener.Start();
        return listener;
    }

    private sealed class FakeAggregateRepository : IAggregateRepository<FakeAggregate, OrderId>
    {
        public ValueTask<Result<FakeAggregate, StoreError>> LoadAsync(OrderId id, CancellationToken ct = default)
            => ValueTask.FromResult(Result<FakeAggregate, StoreError>.Success(new FakeAggregate()));

        public ValueTask<Result<AppendResult, StoreError>> SaveAsync(FakeAggregate aggregate, OrderId id, CancellationToken ct = default)
            => ValueTask.FromResult(Result<AppendResult, StoreError>.Success(new AppendResult(_streamId, StreamPosition.Start)));
    }
}

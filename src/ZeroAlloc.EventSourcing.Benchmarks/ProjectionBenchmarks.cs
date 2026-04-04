using BenchmarkDotNet.Attributes;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks for projection event application and read model updates.
/// Measures performance of single event, multiple sequential events, and typed dispatch.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
[Config(typeof(BenchmarkConfig))]
public class ProjectionBenchmarks
{
    private TestInvoiceProjection _projection = null!;
    private readonly InvoiceCreated _invoiceCreated = new InvoiceCreated { InvoiceId = "INV-001", Amount = 1000m };
    private readonly Paid _paid = new Paid { Amount = 500m };
    private readonly Refund _refund = new Refund { Amount = 100m };

    /// <summary>
    /// Setup method called once before all iterations to initialize the projection.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _projection = new TestInvoiceProjection();
    }

    /// <summary>
    /// Benchmarks applying a single event to a read model.
    /// Measures the overhead of event dispatch and state update for one event.
    /// </summary>
    [Benchmark]
    public void ApplySingleEvent()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.ApplyDynamic(current, _invoiceCreated);
    }

    /// <summary>
    /// Benchmarks applying multiple events sequentially to a read model.
    /// Measures cumulative overhead of multiple event applications and state accumulation.
    /// </summary>
    [Benchmark]
    public void ApplyMultipleEventsSequential()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.ApplyDynamic(current, _invoiceCreated);
        _projection.ApplyDynamic(current, _paid);
        _projection.ApplyDynamic(current, _refund);
    }

    /// <summary>
    /// Benchmarks applying events using typed dispatch (simulating generated dispatch).
    /// Measures performance of type-specific event routing vs. dynamic dispatch.
    /// </summary>
    [Benchmark]
    public void ApplyTypedDispatch()
    {
        var current = new InvoiceReadModel { Id = "INV-001", Total = 0m };
        _projection.ApplyTyped(current, _invoiceCreated);
        _projection.ApplyTyped(current, _paid);
        _projection.ApplyTyped(current, _refund);
    }

    // --- Test domain model for projection benchmarking ---

    /// <summary>
    /// Test read model representing an invoice with basic accounting fields.
    /// </summary>
    public record InvoiceReadModel
    {
        /// <summary>Gets or sets the invoice identifier.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the total invoice amount.</summary>
        public decimal Total { get; set; }

        /// <summary>Gets or sets the paid amount.</summary>
        public decimal Paid { get; set; }
    }

    /// <summary>
    /// Event raised when an invoice is created.
    /// </summary>
    public record InvoiceCreated
    {
        /// <summary>Gets or sets the invoice identifier.</summary>
        public string InvoiceId { get; set; } = string.Empty;

        /// <summary>Gets or sets the invoice amount.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Event raised when an invoice is paid.
    /// </summary>
    public record Paid
    {
        /// <summary>Gets or sets the paid amount.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Event raised when an invoice payment is refunded.
    /// </summary>
    public record Refund
    {
        /// <summary>Gets or sets the refund amount.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>
    /// Test projection that applies invoice events to a read model.
    /// Demonstrates both dynamic and typed dispatch patterns for event routing.
    /// </summary>
    public class TestInvoiceProjection : Projection<InvoiceReadModel>
    {
        /// <summary>
        /// Abstract Apply method implementation. Routes raw events to typed handlers.
        /// Required by the base Projection{TReadModel} class.
        /// </summary>
        protected override InvoiceReadModel Apply(InvoiceReadModel current, EventEnvelope @event)
        {
            // For benchmarking purposes, delegate to the dynamic Apply method.
            // In production, this would use the source-generated ApplyTyped method.
            ApplyDynamic(current, @event.Event);
            return Current;
        }

        /// <summary>
        /// Applies an event to the read model using dynamic dispatch (pattern matching on event type).
        /// </summary>
        public void ApplyDynamic(InvoiceReadModel current, object @event)
        {
            if (@event is InvoiceCreated ic)
                ApplyInvoiceCreated(current, ic);
            else if (@event is Paid p)
                ApplyPaid(current, p);
            else if (@event is Refund r)
                ApplyRefund(current, r);
        }

        /// <summary>
        /// Applies an InvoiceCreated event to the read model.
        /// </summary>
        public void ApplyInvoiceCreated(InvoiceReadModel current, InvoiceCreated @event)
        {
            Current = current with { Id = @event.InvoiceId, Total = @event.Amount };
        }

        /// <summary>
        /// Applies a Paid event to the read model, accumulating payment amount.
        /// </summary>
        public void ApplyPaid(InvoiceReadModel current, Paid @event)
        {
            Current = current with { Paid = current.Paid + @event.Amount };
        }

        /// <summary>
        /// Applies a Refund event to the read model, reducing invoice total.
        /// </summary>
        public void ApplyRefund(InvoiceReadModel current, Refund @event)
        {
            Current = current with { Total = current.Total - @event.Amount };
        }

        /// <summary>
        /// Applies an event using typed switch-based dispatch.
        /// Simulates code-generated typed dispatch (Phase 5, Task 5 source generator).
        /// </summary>
        public void ApplyTyped(InvoiceReadModel current, object @event)
        {
            switch (@event)
            {
                case InvoiceCreated ic:
                    ApplyInvoiceCreated(current, ic);
                    break;
                case Paid p:
                    ApplyPaid(current, p);
                    break;
                case Refund r:
                    ApplyRefund(current, r);
                    break;
            }
        }
    }
}

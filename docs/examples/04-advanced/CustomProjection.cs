using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Examples.Advanced;

/// <summary>
/// This example demonstrates building a custom projection with advanced patterns.
///
/// Projections are the read models of event sourcing.
/// They transform events into optimized views for queries.
///
/// This example shows:
/// 1. Filtering events (only process relevant ones)
/// 2. Multiple read models from one projection
/// 3. Denormalization (pre-computed data)
/// 4. Using the envelope's metadata (when the event occurred)
///
/// This file is compiled and run by the test suite, so it only uses the public API.
/// </summary>

// Domain events
public record CustomerCreatedEvent(string CustomerId, string Name, string Email);
public record CustomerSubscribedEvent(string CustomerId, string PlanId);
public record CustomerUpgradedEvent(string CustomerId, string NewPlanId);
public record CustomerCancelledEvent(string CustomerId);

// Read models. They are immutable: Apply returns a new read model instead of changing the old one.
public sealed record CustomerReadModel(
    string CustomerId,
    string Name,
    string Email,
    string? CurrentPlan,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public override string ToString() =>
        $"{Name} ({Email}) - {(IsActive ? "Active" : "Inactive")}: {CurrentPlan}";
}

public sealed record SubscriptionReadModel(string CustomerId, string PlanId, DateTimeOffset SubscribedAt);

/// <summary>
/// The projection's read model: two views over the same events, plus a count of the events
/// that changed them.
/// </summary>
public sealed record CustomerDirectory(
    ImmutableDictionary<string, CustomerReadModel> Customers,
    ImmutableDictionary<string, SubscriptionReadModel> ActiveSubscriptions,
    int ProcessedEvents)
{
    public static CustomerDirectory Empty { get; } = new(
        ImmutableDictionary<string, CustomerReadModel>.Empty,
        ImmutableDictionary<string, SubscriptionReadModel>.Empty,
        0);
}

/// <summary>
/// Advanced projection with multiple read models and filtering.
/// </summary>
public sealed class CustomerProjection : Projection<CustomerDirectory>
{
    public CustomerProjection()
    {
        // Projection<T>.Current starts at default, which is null for a class: start empty instead.
        Current = CustomerDirectory.Empty;
    }

    protected override CustomerDirectory Apply(CustomerDirectory current, EventEnvelope envelope)
    {
        // The event's timestamp comes from its metadata, so replaying the stream later
        // rebuilds exactly the same read model.
        var occurredAt = envelope.Metadata.OccurredAt;

        var updated = envelope.Event switch
        {
            CustomerCreatedEvent e => ApplyCustomerCreated(current, e, occurredAt),
            CustomerSubscribedEvent e => ApplyCustomerSubscribed(current, e, occurredAt),
            CustomerUpgradedEvent e => ApplyCustomerUpgraded(current, e),
            CustomerCancelledEvent e => ApplyCustomerCancelled(current, e),
            // Ignore other events
            _ => current
        };

        // Count only the events that changed the read model
        return ReferenceEquals(updated, current)
            ? current
            : updated with { ProcessedEvents = updated.ProcessedEvents + 1 };
    }

    private static CustomerDirectory ApplyCustomerCreated(
        CustomerDirectory current, CustomerCreatedEvent e, DateTimeOffset occurredAt)
    {
        var customer = new CustomerReadModel(e.CustomerId, e.Name, e.Email, CurrentPlan: null, IsActive: true, occurredAt);
        return current with { Customers = current.Customers.SetItem(e.CustomerId, customer) };
    }

    private static CustomerDirectory ApplyCustomerSubscribed(
        CustomerDirectory current, CustomerSubscribedEvent e, DateTimeOffset occurredAt)
    {
        // An event for a customer the projection has not seen is ignored
        if (!current.Customers.TryGetValue(e.CustomerId, out var customer))
            return current;

        return current with
        {
            Customers = current.Customers.SetItem(e.CustomerId, customer with { CurrentPlan = e.PlanId }),
            ActiveSubscriptions = current.ActiveSubscriptions.SetItem(
                e.CustomerId, new SubscriptionReadModel(e.CustomerId, e.PlanId, occurredAt)),
        };
    }

    private static CustomerDirectory ApplyCustomerUpgraded(CustomerDirectory current, CustomerUpgradedEvent e)
    {
        if (!current.Customers.TryGetValue(e.CustomerId, out var customer))
            return current;

        var subscriptions = current.ActiveSubscriptions.TryGetValue(e.CustomerId, out var subscription)
            ? current.ActiveSubscriptions.SetItem(e.CustomerId, subscription with { PlanId = e.NewPlanId })
            : current.ActiveSubscriptions;

        return current with
        {
            Customers = current.Customers.SetItem(e.CustomerId, customer with { CurrentPlan = e.NewPlanId }),
            ActiveSubscriptions = subscriptions,
        };
    }

    private static CustomerDirectory ApplyCustomerCancelled(CustomerDirectory current, CustomerCancelledEvent e)
    {
        if (!current.Customers.TryGetValue(e.CustomerId, out var customer))
            return current;

        return current with
        {
            Customers = current.Customers.SetItem(e.CustomerId, customer with { IsActive = false, CurrentPlan = null }),
            ActiveSubscriptions = current.ActiveSubscriptions.Remove(e.CustomerId),
        };
    }

    // ===== Query Methods =====

    /// <summary>Get a customer by ID.</summary>
    public CustomerReadModel? GetCustomer(string customerId)
        => Current.Customers.GetValueOrDefault(customerId);

    /// <summary>Get all active customers.</summary>
    public List<CustomerReadModel> GetActiveCustomers()
        => Current.Customers.Values.Where(c => c.IsActive).OrderBy(c => c.CustomerId, StringComparer.Ordinal).ToList();

    /// <summary>Get all customers on a specific plan.</summary>
    public List<CustomerReadModel> GetCustomersOnPlan(string planId)
        => Current.Customers.Values
            .Where(c => c.IsActive && c.CurrentPlan == planId)
            .OrderBy(c => c.CustomerId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Get active subscription details.</summary>
    public SubscriptionReadModel? GetActiveSubscription(string customerId)
        => Current.ActiveSubscriptions.GetValueOrDefault(customerId);

    /// <summary>Get all active subscriptions for a plan.</summary>
    public List<SubscriptionReadModel> GetSubscriptionsForPlan(string planId)
        => Current.ActiveSubscriptions.Values.Where(s => s.PlanId == planId).ToList();

    /// <summary>Get projection statistics.</summary>
    public (int CustomerCount, int ActiveCount, int ProcessedEvents) GetStats()
        => (Current.Customers.Count, Current.Customers.Values.Count(c => c.IsActive), Current.ProcessedEvents);
}

/// <summary>
/// Usage example showing how to build and use a custom projection.
/// </summary>
public static class CustomProjectionExample
{
    public static async Task<CustomerProjection> RunAsync()
    {
        Console.WriteLine("=== Custom Projection Example ===\n");

        // Create the projection
        var projection = new CustomerProjection();

        // Simulate events (would normally come from the event store or a stream consumer)
        var events = new object[]
        {
            new CustomerCreatedEvent("cust-1", "Alice Smith", "alice@example.com"),
            new CustomerCreatedEvent("cust-2", "Bob Jones", "bob@example.com"),
            new CustomerSubscribedEvent("cust-1", "pro"),
            new CustomerSubscribedEvent("cust-2", "basic"),
            new CustomerUpgradedEvent("cust-2", "pro"),
            new CustomerCreatedEvent("cust-3", "Charlie Brown", "charlie@example.com"),
            new CustomerSubscribedEvent("cust-3", "enterprise"),
            new CustomerCancelledEvent("cust-1"),
        };

        // Apply events. Stream positions are 1-based: the first event is at position 1.
        Console.WriteLine("Processing events...\n");
        var position = StreamPosition.Start;

        foreach (var @event in events)
        {
            position = position.Next();
            var envelope = new EventEnvelope(
                new StreamId("customers"),
                position,
                @event,
                EventMetadata.New(@event.GetType().Name));

            await projection.HandleAsync(envelope);
        }

        // Query the projection
        Console.WriteLine("=== Querying Projection ===\n");

        // Query 1: Get specific customer
        Console.WriteLine("Query 1: Get customer details");
        var alice = projection.GetCustomer("cust-1");
        if (alice != null)
        {
            Console.WriteLine($"  {alice}");
        }

        // Query 2: Get all active customers
        Console.WriteLine("\nQuery 2: All active customers");
        foreach (var customer in projection.GetActiveCustomers())
        {
            Console.WriteLine($"  {customer}");
        }

        // Query 3: Get customers on specific plan
        Console.WriteLine("\nQuery 3: Customers on 'pro' plan");
        foreach (var customer in projection.GetCustomersOnPlan("pro"))
        {
            Console.WriteLine($"  {customer}");
        }

        // Query 4: Get subscriptions for a plan
        Console.WriteLine("\nQuery 4: Count of subscriptions per plan");
        Console.WriteLine($"  Basic: {projection.GetSubscriptionsForPlan("basic").Count}");
        Console.WriteLine($"  Pro: {projection.GetSubscriptionsForPlan("pro").Count}");
        Console.WriteLine($"  Enterprise: {projection.GetSubscriptionsForPlan("enterprise").Count}");

        // Query 5: Get stats
        Console.WriteLine("\nQuery 5: Projection statistics");
        var (totalCustomers, activeCount, processedEvents) = projection.GetStats();
        Console.WriteLine($"  Total customers: {totalCustomers}");
        Console.WriteLine($"  Active customers: {activeCount}");
        Console.WriteLine($"  Events processed: {processedEvents}");

        return projection;
    }
}

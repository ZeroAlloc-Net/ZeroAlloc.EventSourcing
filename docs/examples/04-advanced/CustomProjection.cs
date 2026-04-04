using System;
using System.Collections.Generic;
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
/// 4. State persistence
/// </summary>

// Domain events
public record CustomerCreatedEvent(string CustomerId, string Name, string Email);
public record CustomerSubscribedEvent(string CustomerId, string PlanId);
public record CustomerUpgradedEvent(string CustomerId, string NewPlanId);
public record CustomerCancelledEvent(string CustomerId);

// Read models
public class CustomerReadModel
{
    public string CustomerId { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public string? CurrentPlan { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }

    public override string ToString() =>
        $"{Name} ({Email}) - {(IsActive ? "Active" : "Inactive")}: {CurrentPlan}";
}

public class SubscriptionReadModel
{
    public string CustomerId { get; set; }
    public string PlanId { get; set; }
    public DateTime SubscribedAt { get; set; }
}

/// <summary>
/// Advanced projection with multiple read models and filtering.
/// </summary>
public class CustomerProjection : Projection
{
    // Read model 1: Customer details (one per customer)
    private readonly Dictionary<string, CustomerReadModel> _customers = new();

    // Read model 2: Active subscriptions (for queries)
    private readonly Dictionary<string, SubscriptionReadModel> _activeSubscriptions = new();

    // Metadata: Track processed events
    private int _processedEventCount = 0;

    public override async ValueTask<bool> ApplyAsync(EventEnvelope envelope)
    {
        // Before processing
        var beforeCount = _customers.Count;

        var handled = envelope.Event switch
        {
            CustomerCreatedEvent e => ApplyCustomerCreated(e),
            CustomerSubscribedEvent e => ApplyCustomerSubscribed(e),
            CustomerUpgradedEvent e => ApplyCustomerUpgraded(e),
            CustomerCancelledEvent e => ApplyCustomerCancelled(e),
            // Ignore other events
            _ => false
        };

        if (handled)
            _processedEventCount++;

        return handled;
    }

    private bool ApplyCustomerCreated(CustomerCreatedEvent e)
    {
        // Create new customer
        _customers[e.CustomerId] = new CustomerReadModel
        {
            CustomerId = e.CustomerId,
            Name = e.Name,
            Email = e.Email,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        Console.WriteLine($"  [Customer Created] {e.Name} ({e.Email})");
        return true;
    }

    private bool ApplyCustomerSubscribed(CustomerSubscribedEvent e)
    {
        // Update customer subscription
        if (!_customers.TryGetValue(e.CustomerId, out var customer))
        {
            // Customer doesn't exist - ignore or log warning
            Console.WriteLine($"  [Warning] Subscription event for unknown customer {e.CustomerId}");
            return false;
        }

        // Update customer
        customer.CurrentPlan = e.PlanId;

        // Add to active subscriptions
        _activeSubscriptions[e.CustomerId] = new SubscriptionReadModel
        {
            CustomerId = e.CustomerId,
            PlanId = e.PlanId,
            SubscribedAt = DateTime.UtcNow
        };

        Console.WriteLine($"  [Subscribed] {customer.Name} to plan {e.PlanId}");
        return true;
    }

    private bool ApplyCustomerUpgraded(CustomerUpgradedEvent e)
    {
        // Upgrade customer plan
        if (!_customers.TryGetValue(e.CustomerId, out var customer))
        {
            Console.WriteLine($"  [Warning] Upgrade event for unknown customer {e.CustomerId}");
            return false;
        }

        var oldPlan = customer.CurrentPlan;
        customer.CurrentPlan = e.NewPlanId;

        // Update subscription
        if (_activeSubscriptions.TryGetValue(e.CustomerId, out var sub))
            sub.PlanId = e.NewPlanId;

        Console.WriteLine($"  [Upgraded] {customer.Name} from {oldPlan} to {e.NewPlanId}");
        return true;
    }

    private bool ApplyCustomerCancelled(CustomerCancelledEvent e)
    {
        // Mark customer as inactive
        if (!_customers.TryGetValue(e.CustomerId, out var customer))
        {
            Console.WriteLine($"  [Warning] Cancellation event for unknown customer {e.CustomerId}");
            return false;
        }

        customer.IsActive = false;
        customer.CurrentPlan = null;

        // Remove from active subscriptions
        _activeSubscriptions.Remove(e.CustomerId);

        Console.WriteLine($"  [Cancelled] {customer.Name}");
        return true;
    }

    // ===== Query Methods =====

    /// <summary>Get a customer by ID.</summary>
    public CustomerReadModel? GetCustomer(string customerId)
    {
        _customers.TryGetValue(customerId, out var customer);
        return customer;
    }

    /// <summary>Get all active customers.</summary>
    public List<CustomerReadModel> GetActiveCustomers()
    {
        return _customers.Values.Where(c => c.IsActive).ToList();
    }

    /// <summary>Get all customers on a specific plan.</summary>
    public List<CustomerReadModel> GetCustomersOnPlan(string planId)
    {
        return _customers.Values
            .Where(c => c.IsActive && c.CurrentPlan == planId)
            .ToList();
    }

    /// <summary>Get active subscription details.</summary>
    public SubscriptionReadModel? GetActiveSubscription(string customerId)
    {
        _activeSubscriptions.TryGetValue(customerId, out var sub);
        return sub;
    }

    /// <summary>Get all active subscriptions for a plan.</summary>
    public List<SubscriptionReadModel> GetSubscriptionsForPlan(string planId)
    {
        return _activeSubscriptions.Values
            .Where(s => s.PlanId == planId)
            .ToList();
    }

    /// <summary>Get projection statistics.</summary>
    public (int CustomerCount, int ActiveCount, int ProcessedEvents) GetStats()
    {
        var activeCount = _customers.Values.Count(c => c.IsActive);
        return (_customers.Count, activeCount, _processedEventCount);
    }
}

/// <summary>
/// Usage example showing how to build and use a custom projection.
/// </summary>
public class CustomProjectionExample
{
    public static async Task Main()
    {
        Console.WriteLine("=== Custom Projection Example ===\n");

        // Create the projection
        var projection = new CustomerProjection();

        // Simulate events (would normally come from event store)
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

        // Apply events
        Console.WriteLine("Processing events...\n");
        var position = StreamPosition.Start;

        foreach (var @event in events)
        {
            var envelope = new EventEnvelope(
                new StreamId("customers"),
                position,
                @event,
                DateTimeOffset.UtcNow,
                new EventMetadata()
            );

            await projection.ApplyAsync(envelope);
            position = position.Next();
        }

        // Query the projection
        Console.WriteLine("\n=== Querying Projection ===\n");

        // Query 1: Get specific customer
        Console.WriteLine("Query 1: Get customer details");
        var alice = projection.GetCustomer("cust-1");
        if (alice != null)
        {
            Console.WriteLine($"  {alice}");
        }

        // Query 2: Get all active customers
        Console.WriteLine("\nQuery 2: All active customers");
        var activeCustomers = projection.GetActiveCustomers();
        foreach (var customer in activeCustomers)
        {
            Console.WriteLine($"  {customer}");
        }

        // Query 3: Get customers on specific plan
        Console.WriteLine("\nQuery 3: Customers on 'pro' plan");
        var proCustomers = projection.GetCustomersOnPlan("pro");
        foreach (var customer in proCustomers)
        {
            Console.WriteLine($"  {customer}");
        }

        // Query 4: Get subscriptions for a plan
        Console.WriteLine("\nQuery 4: Count of subscriptions per plan");
        var basicSubs = projection.GetSubscriptionsForPlan("basic");
        var proSubs = projection.GetSubscriptionsForPlan("pro");
        var enterpriseSubs = projection.GetSubscriptionsForPlan("enterprise");
        Console.WriteLine($"  Basic: {basicSubs.Count}");
        Console.WriteLine($"  Pro: {proSubs.Count}");
        Console.WriteLine($"  Enterprise: {enterpriseSubs.Count}");

        // Query 5: Get stats
        Console.WriteLine("\nQuery 5: Projection statistics");
        var (totalCustomers, activeCount, processedEvents) = projection.GetStats();
        Console.WriteLine($"  Total customers: {totalCustomers}");
        Console.WriteLine($"  Active customers: {activeCount}");
        Console.WriteLine($"  Events processed: {processedEvents}");
    }
}

namespace ZeroAlloc.EventSourcing.Aggregates.Tests.Lifecycle;

public record OrderPlaced(decimal Total);
public record OrderShipped(string TrackingNumber);
public record OrderCancelled(string Reason);

using System;
using ZeroAlloc.EventSourcing.AotSmoke;

// Exercise the generator-backed Aggregate<TId, TState> + Raise/ApplyEvent flow
// under PublishAot=true. Confirms events persist into the uncommitted list
// and state transitions through ApplyEvent without reflection.

using var order = new Order();
order.SetId(new OrderId(Guid.NewGuid()));
order.Place(orderId: "ord-1", total: 99.95m);
order.Ship(tracking: "pkg-1");

if (!order.State.IsPlaced)
{
    Console.Error.WriteLine("AOT smoke: FAIL — state.IsPlaced should be true after Place");
    return 1;
}
if (!order.State.IsShipped)
{
    Console.Error.WriteLine("AOT smoke: FAIL — state.IsShipped should be true after Ship");
    return 1;
}
if (order.State.Total != 99.95m)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — Total expected 99.95, got {order.State.Total}");
    return 1;
}

var uncommitted = ((ZeroAlloc.EventSourcing.Aggregates.IAggregate)order).DequeueUncommitted();
if (uncommitted.Length != 2)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — expected 2 uncommitted events, got {uncommitted.Length}");
    return 1;
}

Console.WriteLine("AOT smoke: PASS");
return 0;

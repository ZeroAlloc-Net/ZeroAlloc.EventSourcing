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

// DequeueUncommitted is internal; Version is the public bookkeeping signal.
if (order.Version.Value != 2)
{
    Console.Error.WriteLine($"AOT smoke: FAIL — Version expected 2 after two Raise() calls, got {order.Version.Value}");
    return 1;
}

Console.WriteLine("AOT smoke: PASS");
return 0;

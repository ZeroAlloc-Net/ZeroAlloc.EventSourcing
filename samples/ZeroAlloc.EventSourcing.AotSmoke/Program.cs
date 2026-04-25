using System;
using ZeroAlloc.EventSourcing.AotSmoke;

// Exercise the generator-backed Aggregate<TId, TState> + Raise/ApplyEvent flow
// AND the ZeroAlloc.StateMachine companion FSM transition validation under
// PublishAot=true. Confirms events persist into the uncommitted list, state
// transitions through ApplyEvent, and illegal transitions throw without
// reflection.

// Path 1: happy path — Place then Ship
{
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
    if (order.Version.Value != 2)
    {
        Console.Error.WriteLine($"AOT smoke: FAIL — Version expected 2, got {order.Version.Value}");
        return 1;
    }
}

// Path 2: compensation path — Place then Cancel, then Ship must throw
{
    using var order = new Order();
    order.SetId(new OrderId(Guid.NewGuid()));
    order.Place(orderId: "ord-2", total: 50m);
    order.Cancel(reason: "AOT smoke compensation test");

    if (!order.State.IsCancelled)
    {
        Console.Error.WriteLine("AOT smoke: FAIL — state.IsCancelled should be true after Cancel");
        return 1;
    }

    try
    {
        order.Ship(tracking: "should-throw");
        Console.Error.WriteLine("AOT smoke: FAIL — Ship after Cancel should have thrown InvalidOperationException");
        return 1;
    }
    catch (InvalidOperationException)
    {
        // expected — FSM rejected the illegal transition
    }
}

Console.WriteLine("AOT smoke: PASS");
return 0;

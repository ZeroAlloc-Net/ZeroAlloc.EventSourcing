# Diagnostics

The source generators in `ZeroAlloc.EventSourcing.Generators` report these diagnostics. Each one
means that nothing is generated for the aggregate or projection it points at.

| ID | Severity | Title |
|----|----------|-------|
| [ZAES005](#zaes005) | Warning | Containing type of an aggregate or projection is not partial |
| [ZAES006](#zaes006) | Warning | Generic aggregate or projection is not generated |
| [ZAES007](#zaes007) | Error | File-local aggregate or projection is not generated |

`ZAES001` to `ZAES004` are the diagnostic IDs of obsolete APIs, reported by the compiler through
`[Obsolete]`; they are not generator diagnostics.

## ZAES005

**Containing type of an aggregate or projection is not partial.**

An aggregate or projection nested in another type is generated into the real nested type. To do
that, the generated file reopens every containing type as `partial`, so each containing type has to
be declared `partial`. When one is not, the generator reports ZAES005 on the aggregate or projection,
naming the outermost containing type that is not partial, and generates nothing for it.

```csharp
public static class Retail                  // ZAES005: Retail is not partial
{
    public sealed partial class Order : Aggregate<OrderId, OrderState> { }
}
```

Fix it by declaring every containing type `partial`:

```csharp
public static partial class Retail
{
    public sealed partial class Order : Aggregate<OrderId, OrderState> { }
}
```

If you do not want the type generated, declare it without `partial` and write its dispatch by
hand, as for [ZAES006](#zaes006). An aggregate that already overrides `ApplyEvent` by hand is never
generated, so it gets no ZAES005.

## ZAES006

**Generic aggregate or projection is not generated.**

The generator does not generate a generic aggregate or projection, such as `Order<TTag>`. The event
type registry of an aggregate is a class at namespace level, and it cannot name an open generic
type. For the same reason, an aggregate nested in a generic type is not generated when one of its
event types uses a type parameter of that containing type, for example `Module<T>.Placed(T Value)`.
The generator reports ZAES006 on the type and generates nothing for it.

An aggregate or projection nested in a generic type whose event types do not use a type parameter
is generated as usual.

Fix it by writing the dispatch by hand and declaring the type without `partial`: the generators only
look at partial classes. For an aggregate, write the `ApplyEvent` override and an `IEventTypeRegistry`
for its events. For a projection, write the `Apply(TReadModel, EventEnvelope)` override without
calling `ApplyTyped`. An aggregate that overrides `ApplyEvent` by hand gets no ZAES006 even when it is
`partial`.

```csharp
public sealed class Order<TTag> : Aggregate<OrderId, OrderState>
{
    protected override OrderState ApplyEvent(OrderState state, object @event) => @event switch
    {
        OrderPlaced e => state.Apply(e),
        _ => state,
    };
}
```

## ZAES007

**File-local aggregate or projection is not generated.**

A `file` type is visible only in the source file that declares it, so the generated file cannot
extend it. The generator reports the error ZAES007 on an aggregate or projection that is declared
`file`, or that is nested in a `file` type, and generates nothing for it. Other aggregates and
projections in the project are still generated.

```csharp
file sealed partial class Order : Aggregate<OrderId, OrderState> { }   // ZAES007
```

Fix it by removing the `file` modifier, for example by making the type `internal`:

```csharp
internal sealed partial class Order : Aggregate<OrderId, OrderState> { }
```

To keep the type file-local, declare it without `partial` and write its dispatch by hand, as for
[ZAES006](#zaes006): the generators only look at partial classes.

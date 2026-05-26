using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.EventSourcing.Mediator;

/// <summary>
/// Dispatches an ES event payload to Mediator's typed <c>Publish&lt;T&gt;</c>. The bridge
/// package declares this interface; the bundled source generator emits the concrete
/// implementation that switches over every <see cref="INotification"/> type discovered in
/// the consuming compilation. Users never implement this interface themselves.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>
    /// Dispatches <paramref name="event"/> via <c>IMediator.Publish&lt;TConcrete&gt;</c>.
    /// Returns <see cref="ValueTask.CompletedTask"/> for events that aren't a discovered
    /// <see cref="INotification"/> type (silent skip).
    /// </summary>
    ValueTask DispatchAsync(object @event, CancellationToken ct);
}

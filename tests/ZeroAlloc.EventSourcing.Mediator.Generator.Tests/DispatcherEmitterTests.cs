using System.Linq;
using System.Threading.Tasks;
using VerifyXunit;

namespace ZeroAlloc.EventSourcing.Mediator.Generator.Tests;

public sealed class DispatcherEmitterTests
{
    [Fact]
    public Task SingleNotificationType_SnapshotMatches() => TestHarness.Verify("""
        using ZeroAlloc.Mediator;
        namespace MyApp;
        public readonly record struct UserCreated(System.Guid Id) : INotification;
        """);

    [Fact]
    public Task MultipleNotificationTypes_SnapshotMatches() => TestHarness.Verify("""
        using ZeroAlloc.Mediator;
        namespace MyApp;
        public readonly record struct UserCreated(System.Guid Id) : INotification;
        public readonly record struct OrderPlaced(decimal Total) : INotification;
        """);

    [Fact]
    public Task EmptyCompilation_FiresZESM001_AndStillEmitsExtension()
    {
        var diags = TestHarness.RunDiagnostics("namespace MyApp;");
        Assert.Contains(diags, d => d.Id == "ZESM001");
        // Also confirm the generator still produced source output even with zero notifications.
        // We assert behaviour via a snapshot verification on an empty user-source compilation:
        return TestHarness.Verify("namespace MyApp;");
    }
}

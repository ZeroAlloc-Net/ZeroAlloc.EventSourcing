using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Outbox;

namespace ZeroAlloc.EventSourcing.Outbox.Tests;

public class OutboxOptionsTests
{
    [Fact]
    public void Defaults_match_design_contract()
    {
        var opts = new OutboxOptions();

        opts.ConsumerId.Should().Be("outbox");
        opts.BatchSize.Should().Be(100);
        opts.ErrorStrategy.Should().Be(ErrorHandlingStrategy.DeadLetter);
        opts.CommitStrategy.Should().Be(CommitStrategy.AfterEvent);
        opts.PollInterval.Should().Be(TimeSpan.FromSeconds(1));
        opts.ExcludedTypes.Should().BeEmpty();
    }

    [Fact]
    public void Exclude_adds_type_and_returns_options_for_chaining()
    {
        var opts = new OutboxOptions();
        var returned = opts.Exclude<NotifA>();

        returned.Should().BeSameAs(opts);
        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(NotifA));
    }

    [Fact]
    public void Exclude_is_idempotent()
    {
        var opts = new OutboxOptions();
        opts.Exclude<NotifA>().Exclude<NotifA>();

        opts.ExcludedTypes.Should().ContainSingle().Which.Should().Be(typeof(NotifA));
    }

    [Fact]
    public void Exclude_supports_multiple_distinct_types()
    {
        var opts = new OutboxOptions();
        opts.Exclude<NotifA>().Exclude<NotifB>();

        opts.ExcludedTypes.Should().BeEquivalentTo([typeof(NotifA), typeof(NotifB)]);
    }

    private sealed class NotifA { }
    private sealed class NotifB { }
}

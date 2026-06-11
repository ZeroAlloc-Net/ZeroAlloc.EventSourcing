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

    [Fact]
    public void BatchSize_setter_rejects_zero_or_negative()
    {
        var opts = new OutboxOptions();
        var setZero = () => opts.BatchSize = 0;
        var setNeg = () => opts.BatchSize = -1;

        setZero.Should().Throw<ArgumentOutOfRangeException>();
        setNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxRetries_setter_rejects_negative()
    {
        var opts = new OutboxOptions();
        var setNeg = () => opts.MaxRetries = -1;

        setNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MaxRetries_setter_accepts_zero()
    {
        var opts = new OutboxOptions();
        opts.MaxRetries = 0;   // 0 means "do not retry"; valid
        opts.MaxRetries.Should().Be(0);
    }

    [Fact]
    public void PollInterval_setter_rejects_negative()
    {
        var opts = new OutboxOptions();
        var setNeg = () => opts.PollInterval = TimeSpan.FromSeconds(-1);

        setNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConsumerId_setter_rejects_null_or_whitespace()
    {
        var opts = new OutboxOptions();
        var setNull = () => opts.ConsumerId = null!;
        var setEmpty = () => opts.ConsumerId = "";
        var setWhitespace = () => opts.ConsumerId = "   ";

        setNull.Should().Throw<ArgumentException>();
        setEmpty.Should().Throw<ArgumentException>();
        setWhitespace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RetryPolicy_setter_rejects_null()
    {
        var opts = new OutboxOptions();
        var setNull = () => opts.RetryPolicy = null!;

        setNull.Should().Throw<ArgumentNullException>();
    }

    private sealed class NotifA { }
    private sealed class NotifB { }
}

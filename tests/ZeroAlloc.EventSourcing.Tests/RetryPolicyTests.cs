using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class ExponentialBackoffRetryPolicyTests
{
    [Fact]
    public void GetDelay_FirstAttempt_ReturnsInitialDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 5000);
        var delay = policy.GetDelay(attemptNumber: 1);

        delay.TotalMilliseconds.Should().Be(100);
    }

    [Fact]
    public void GetDelay_SecondAttempt_DoublesPreviousDelay()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 5000);
        var delay1 = policy.GetDelay(attemptNumber: 1);
        var delay2 = policy.GetDelay(attemptNumber: 2);

        delay2.TotalMilliseconds.Should().Be(200);
    }

    [Fact]
    public void GetDelay_ExceedsMaxDelay_CapsAtMax()
    {
        var policy = new ExponentialBackoffRetryPolicy(initialDelayMs: 100, maxDelayMs: 500);
        var delay10 = policy.GetDelay(attemptNumber: 10);

        delay10.TotalMilliseconds.Should().Be(500);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_InvalidValues_ThrowsArgumentException(int value)
    {
        FluentActions.Invoking(() =>
            new ExponentialBackoffRetryPolicy(initialDelayMs: value, maxDelayMs: 5000))
            .Should().Throw<ArgumentException>();
    }
}

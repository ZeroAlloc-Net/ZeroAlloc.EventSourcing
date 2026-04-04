using FluentAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public class StreamConsumerOptionsTests
{
    [Fact]
    public void DefaultValues_AreReasonable()
    {
        var options = new StreamConsumerOptions();

        options.BatchSize.Should().Be(100);
        options.MaxRetries.Should().Be(3);
        options.RetryPolicy.Should().BeOfType<ExponentialBackoffRetryPolicy>();
        options.ErrorStrategy.Should().Be(ErrorHandlingStrategy.FailFast);
        options.CommitStrategy.Should().Be(CommitStrategy.AfterBatch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void BatchSize_OutOfRange_ThrowsArgumentOutOfRangeException(int value)
    {
        var options = new StreamConsumerOptions();
        FluentActions.Invoking(() => options.BatchSize = value)
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(10000)]
    public void BatchSize_ValidRange_Accepted(int value)
    {
        var options = new StreamConsumerOptions { BatchSize = value };
        options.BatchSize.Should().Be(value);
    }

    [Theory]
    [InlineData(-1)]
    public void MaxRetries_Negative_ThrowsArgumentOutOfRangeException(int value)
    {
        var options = new StreamConsumerOptions();
        FluentActions.Invoking(() => options.MaxRetries = value)
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}

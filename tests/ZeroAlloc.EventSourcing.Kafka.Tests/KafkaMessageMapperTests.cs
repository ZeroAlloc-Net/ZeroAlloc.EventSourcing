#pragma warning disable CS8601 // Possible null reference assignment.
using Confluent.Kafka;
using FluentAssertions;
using NSubstitute;

namespace ZeroAlloc.EventSourcing.Kafka.Tests;

public class KafkaMessageMapperTests
{
    private static ConsumeResult<string, byte[]> BuildMessage(
        string key = "stream-1",
        long offset = 100,
        string topic = "test-topic",
        int partition = 0,
        Headers? headers = null)
    {
        return new()
        {
            Message = new()
            {
                Key = key,
                Value = [1, 2, 3, 4],
                Headers = headers ?? new Headers()
            },
            Topic = topic,
            Partition = partition,
            Offset = offset,
            IsPartitionEOF = false,
            TopicPartitionOffset = new TopicPartitionOffset(topic, partition, offset)
        };
    }

    private static Headers HeadersWithEventType(string eventType)
    {
        var headers = new Headers();
        headers.Add("event-type", System.Text.Encoding.UTF8.GetBytes(eventType));
        return headers;
    }

    private static Headers HeadersWithEventType(string eventType, string eventId)
    {
        var headers = HeadersWithEventType(eventType);
        headers.Add("event-id", System.Text.Encoding.UTF8.GetBytes(eventId));
        return headers;
    }

    [Fact]
    public void ToEnvelope_WithValidHeaders_CreatesEventEnvelope()
    {
        // Arrange
        var headers = HeadersWithEventType("TestEvent", "550e8400-e29b-41d4-a716-446655440000");
        var message = BuildMessage("order-123", headers: headers);

        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();
        var testEvent = new TestEvent { Value = 42 };

        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), typeof(TestEvent))
            .Returns(testEvent);
        registry.TryGetType("TestEvent", out Arg.Any<Type>())
            .Returns(x => { x[1] = typeof(TestEvent); return true; });

        // Act
        var envelope = KafkaMessageMapper.ToEnvelope(message, serializer, registry);

        // Assert
        envelope.StreamId.Should().Be(new StreamId("order-123"));
        envelope.Position.Should().Be(new StreamPosition(100));
        envelope.Event.Should().Be(testEvent);
        envelope.Metadata.EventType.Should().Be("TestEvent");
        envelope.Metadata.EventId.Should().Be(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"));
    }

    [Fact]
    public void ToEnvelope_WithNullKey_UsesTopic()
    {
        // Arrange
        var headers = HeadersWithEventType("TestEvent");
        var message = BuildMessage(key: null!, headers: headers);

        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>())
            .Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>())
            .Returns(x => { x[1] = typeof(TestEvent); return true; });

        // Act
        var envelope = KafkaMessageMapper.ToEnvelope(message, serializer, registry);

        // Assert
        envelope.StreamId.Should().Be(new StreamId("test-topic"));
    }

    [Fact]
    public void ToEnvelope_MissingEventTypeHeader_Throws()
    {
        // Arrange
        var message = BuildMessage(headers: new Headers());
        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        // Act
        Action act = () => KafkaMessageMapper.ToEnvelope(message, serializer, registry);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Missing 'event-type' header*");
    }

    [Fact]
    public void ToEnvelope_UnknownEventType_Throws()
    {
        // Arrange
        var headers = HeadersWithEventType("UnknownEvent");
        var message = BuildMessage(headers: headers);

        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        registry.TryGetType("UnknownEvent", out Arg.Any<Type>())
            .Returns(false);

        // Act
        Action act = () => KafkaMessageMapper.ToEnvelope(message, serializer, registry);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown event type 'UnknownEvent'*");
    }

    [Fact]
    public void ToEnvelope_MissingEventIdHeader_GeneratesNewGuid()
    {
        // Arrange
        var headers = HeadersWithEventType("TestEvent");
        var message = BuildMessage(headers: headers);

        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>())
            .Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>())
            .Returns(x => { x[1] = typeof(TestEvent); return true; });

        // Act
        var envelope = KafkaMessageMapper.ToEnvelope(message, serializer, registry);

        // Assert
        envelope.Metadata.EventId.Should().NotBeEmpty();
    }

    [Fact]
    public void ToEnvelope_MissingOccurredAtHeader_UsesUtcNow()
    {
        // Arrange
        var headers = HeadersWithEventType("TestEvent");
        var message = BuildMessage(headers: headers);
        var before = DateTimeOffset.UtcNow;

        var serializer = Substitute.For<IEventSerializer>();
        var registry = Substitute.For<IEventTypeRegistry>();

        serializer.Deserialize(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<Type>())
            .Returns(new TestEvent());
        registry.TryGetType("TestEvent", out Arg.Any<Type>())
            .Returns(x => { x[1] = typeof(TestEvent); return true; });

        // Act
        var envelope = KafkaMessageMapper.ToEnvelope(message, serializer, registry);
        var after = DateTimeOffset.UtcNow;

        // Assert
        envelope.Metadata.OccurredAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void ToStreamPosition_ConvertsOffset()
    {
        // Act
        var position = KafkaMessageMapper.ToStreamPosition(new Offset(12345));

        // Assert
        position.Should().Be(new StreamPosition(12345));
    }

    [Fact]
    public void ToOffset_ConvertsPosition()
    {
        // Act
        var offset = KafkaMessageMapper.ToOffset(new StreamPosition(12345));

        // Assert
        offset.Should().Be(new Offset(12345));
    }

    private class TestEvent
    {
        public int Value { get; set; }
    }
}

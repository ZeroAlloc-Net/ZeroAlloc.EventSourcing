using NSubstitute;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Tests;

public class ZeroAllocEventSerializerTests
{
    private readonly ISerializerDispatcher _dispatcher = Substitute.For<ISerializerDispatcher>();
    private readonly ZeroAllocEventSerializer _sut;

    public ZeroAllocEventSerializerTests()
        => _sut = new ZeroAllocEventSerializer(_dispatcher);

    [Fact]
    public void Constructor_NullDispatcher_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ZeroAllocEventSerializer(null!));
    }

    [Fact]
    public void Serialize_DelegatesToDispatcher()
    {
        var @event = new TestEvent("hello");
        var expected = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        _dispatcher.Serialize(@event, typeof(TestEvent)).Returns(expected);

        var result = _sut.Serialize(@event);

        Assert.Equal(expected, result);
        _dispatcher.Received(1).Serialize(@event, typeof(TestEvent));
    }

    [Fact]
    public void Deserialize_DelegatesToDispatcher()
    {
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 });
        var expected = new TestEvent("world");
        _dispatcher.Deserialize(payload, typeof(TestEvent)).Returns(expected);

        var result = _sut.Deserialize(payload, typeof(TestEvent));

        Assert.Equal(expected, result);
        _dispatcher.Received(1).Deserialize(payload, typeof(TestEvent));
    }

    [Fact]
    public void Deserialize_DispatcherReturnsNull_Throws()
    {
        var payload = new ReadOnlyMemory<byte>(new byte[] { 1 });
        _dispatcher.Deserialize(payload, typeof(TestEvent)).Returns((object?)null);

        Assert.Throws<InvalidOperationException>(() =>
            _sut.Deserialize(payload, typeof(TestEvent)));
    }

    private sealed record TestEvent(string Value);
}

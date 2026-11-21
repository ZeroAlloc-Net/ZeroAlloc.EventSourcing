using System.Text;
using AwesomeAssertions;
using Xunit;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Tests;

public abstract class ProjectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    /// <summary>The UTF-8 bytes of <paramref name="text"/>, what a text serializer writes.</summary>
    protected static ReadOnlyMemory<byte> Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The loaded state decoded as UTF-8, or <see langword="null"/> when nothing was loaded.</summary>
    protected static string? Text(ReadOnlyMemory<byte>? state)
        => state is { } bytes ? Encoding.UTF8.GetString(bytes.Span) : null;

    [Fact]
    public async Task Load_NonExistentKey_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.LoadAsync("missing-key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", Utf8("""{"count":5}"""));
        var result = await store.LoadAsync("orders");
        Text(result).Should().Be("""{"count":5}""");
    }

    [Fact]
    public async Task Save_Overwrites_ExistingState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", Utf8("""{"count":1}"""));
        await store.SaveAsync("orders", Utf8("""{"count":2}"""));
        var result = await store.LoadAsync("orders");
        Text(result).Should().Be("""{"count":2}""");
    }

    [Fact]
    public async Task Clear_RemovesState()
    {
        var store = CreateStore();
        await store.SaveAsync("orders", Utf8("""{"count":1}"""));
        await store.ClearAsync("orders");
        var result = await store.LoadAsync("orders");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Clear_NonExistentKey_DoesNotThrow()
    {
        var store = CreateStore();
        var act = async () => await store.ClearAsync("nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        var store = CreateStore();
        await store.SaveAsync("proj-a", Utf8("state-a"));
        await store.SaveAsync("proj-b", Utf8("state-b"));
        Text(await store.LoadAsync("proj-a")).Should().Be("state-a");
        Text(await store.LoadAsync("proj-b")).Should().Be("state-b");
    }

    /// <summary>
    /// Binary serializer output, such as MemoryPack or MessagePack writes, round-trips byte for byte:
    /// NUL bytes, which a Postgres TEXT column rejects, and bytes that are not valid UTF-8. #425.
    /// </summary>
    [Fact]
    public async Task BinaryState_RoundTripsByteForByte()
    {
        var store = CreateStore();
        byte[] state = [0x00, 0x01, 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0x7F];

        await store.SaveAsync("binary", state);

        var loaded = await store.LoadAsync("binary");
        loaded.Should().NotBeNull();
        loaded!.Value.ToArray().Should().Equal(state);
    }

    [Fact]
    public async Task EmptyState_LoadsAsEmpty_NotAsMissing()
    {
        var store = CreateStore();

        await store.SaveAsync("empty", ReadOnlyMemory<byte>.Empty);

        var loaded = await store.LoadAsync("empty");
        loaded.Should().NotBeNull();
        loaded!.Value.Length.Should().Be(0);
    }

    /// <summary>
    /// An empty slice of a larger buffer, such as a rented buffer with nothing written yet, saves an
    /// empty state, not the rest of the buffer.
    /// </summary>
    [Fact]
    public async Task EmptySliceOfALargerBuffer_SavesEmpty()
    {
        var store = CreateStore();
        var buffer = new byte[8];

        await store.SaveAsync("empty-slice", buffer.AsMemory(3, 0));

        var loaded = await store.LoadAsync("empty-slice");
        loaded.Should().NotBeNull();
        loaded!.Value.Length.Should().Be(0);
    }

    /// <summary>A state that is a slice of a larger buffer saves only the slice.</summary>
    [Fact]
    public async Task SliceOfALargerBuffer_SavesOnlyTheSlice()
    {
        var store = CreateStore();
        var buffer = Encoding.UTF8.GetBytes("xx{\"count\":3}yy");

        await store.SaveAsync("slice", buffer.AsMemory(2, buffer.Length - 4));

        Text(await store.LoadAsync("slice")).Should().Be("""{"count":3}""");
    }

    /// <summary>The caller may reuse its buffer once the save completed.</summary>
    [Fact]
    public async Task ReusingTheBufferAfterSave_DoesNotChangeTheSavedState()
    {
        var store = CreateStore();
        var buffer = Encoding.UTF8.GetBytes("first");

        await store.SaveAsync("reused", buffer);
        Encoding.UTF8.GetBytes("XXXXX").CopyTo(buffer, 0);

        Text(await store.LoadAsync("reused")).Should().Be("first");
    }
}

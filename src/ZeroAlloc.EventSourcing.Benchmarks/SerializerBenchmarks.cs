using BenchmarkDotNet.Attributes;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.EventSourcing.Benchmarks;

/// <summary>
/// Benchmarks comparing <see cref="BenchmarkSerializer"/> (System.Text.Json reflection) against
/// <see cref="ZeroAllocEventSerializer"/> backed by a hand-wired <see cref="ISerializerDispatcher"/>.
///
/// In production the dispatcher is source-generated (compile-time switch, no reflection),
/// so the real gap is even larger than what this benchmark shows.
/// </summary>
[SimpleJob(warmupCount: 3, invocationCount: 5)]
[MemoryDiagnoser]
public class SerializerBenchmarks
{
    private BenchmarkEvent _event = null!;
    private ReadOnlyMemory<byte> _serialized;
    private BenchmarkSerializer _reflectionSerializer = null!;
    private ZeroAllocEventSerializer _dispatchSerializer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _event = new BenchmarkEvent("bench-id", new byte[] { 1, 2, 3, 4, 5 });
        _reflectionSerializer = new BenchmarkSerializer();
        _dispatchSerializer = new ZeroAllocEventSerializer(new JsonDispatcher());
        _serialized = _reflectionSerializer.Serialize(_event);
    }

    [Benchmark(Baseline = true)]
    public ReadOnlyMemory<byte> Reflection_Serialize() => _reflectionSerializer.Serialize(_event);

    [Benchmark]
    public ReadOnlyMemory<byte> Dispatcher_Serialize() => _dispatchSerializer.Serialize(_event);

    [Benchmark]
    public object Reflection_Deserialize() => _reflectionSerializer.Deserialize(_serialized, typeof(BenchmarkEvent));

    [Benchmark]
    public object Dispatcher_Deserialize() => _dispatchSerializer.Deserialize(_serialized, typeof(BenchmarkEvent));
}

/// <summary>
/// Hand-wired <see cref="ISerializerDispatcher"/> backed by System.Text.Json reflection.
/// Used in benchmarks to measure <see cref="ZeroAllocEventSerializer"/> dispatch overhead
/// in isolation. In production, replace with the source-generated <c>SerializerDispatcher</c>.
/// </summary>
internal sealed class JsonDispatcher : ISerializerDispatcher
{
    public ReadOnlyMemory<byte> Serialize(object value, Type type)
        => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, type);

    public object? Deserialize(ReadOnlyMemory<byte> data, Type type)
        => System.Text.Json.JsonSerializer.Deserialize(data.Span, type);
}

using System.Diagnostics.CodeAnalysis;

namespace ZeroAlloc.EventSourcing.Kafka;

/// <summary>
/// Keeps the librdkafka binding methods of Confluent.Kafka in a trimmed or NativeAOT app.
/// </summary>
/// <remarks>
/// Confluent.Kafka binds to librdkafka the first time it builds a client: it reflects over one of
/// its <c>NativeMethods</c> classes and makes a delegate from each P/Invoke method it finds by
/// name. Nothing calls those methods directly, so the trimmer removes them, and building any
/// consumer, producer or admin client then throws "Sequence contains no matching element" from
/// <c>Librdkafka.SetDelegates</c>. That is the IL2067 warning Confluent.Kafka 2.16.0 reports for
/// <c>SetDelegates</c>, and the reason this package could not run under NativeAOT at all.
/// <para>
/// Every place this package builds a Confluent client calls <see cref="Keep"/> first, so the
/// methods stay whenever this package can build one. Remove this once Confluent.Kafka keeps them
/// itself: confluentinc/confluent-kafka-dotnet#2146.
/// </para>
/// </remarks>
internal static class LibrdkafkaNativeMethods
{
    private const DynamicallyAccessedMemberTypes Methods =
        DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods;

    /// <summary>Marks the binding classes for the trimmer; it does nothing at run time.</summary>
    [DynamicDependency(Methods, "Confluent.Kafka.Impl.NativeMethods.NativeMethods", "Confluent.Kafka")]
    [DynamicDependency(Methods, "Confluent.Kafka.Impl.NativeMethods.NativeMethods_Alpine", "Confluent.Kafka")]
    [DynamicDependency(Methods, "Confluent.Kafka.Impl.NativeMethods.NativeMethods_Centos8", "Confluent.Kafka")]
    public static void Keep()
    {
    }
}

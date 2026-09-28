using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ZeroAlloc.EventSourcing.Aggregates;

namespace ZeroAlloc.EventSourcing.AotSmoke;

#pragma warning disable MA0048 // co-located domain types for a compact sample

// Value-type coverage for NativeAOT. Everything here is a struct or carries nullable value-type
// fields, because value-type generic instantiations get their own native code under AOT and
// have hung where the JIT did not: ZeroAlloc.Cache#182, upstream dotnet/runtime#134799.
// Unlike Order in Domain.cs, Account does NOT hand-write ApplyEvent, so the generator-emitted
// dispatch and event type registry are what run here.

public readonly record struct AccountId(Guid Value);

/// <summary>Struct event with nullable value-type fields.</summary>
public readonly record struct AccountOpened(decimal? OverdraftLimit, int? Tier);

/// <summary>Struct event with a nullable value-type field.</summary>
public readonly record struct FundsDeposited(decimal Amount, DateTimeOffset? ValueDate);

/// <summary>Class event whose only field is a nullable value type.</summary>
public sealed record OverdraftLimitChanged(decimal? NewLimit);

/// <summary>
/// The shape FundsDeposited had before ValueDate existed. Stored streams may still hold it;
/// a struct-to-struct upcaster turns it into <see cref="FundsDeposited"/> on read.
/// </summary>
public readonly record struct FundsDepositedV1(decimal Amount);

public partial record struct AccountState : IAggregateState<AccountState>
{
    public static AccountState Initial => default;

    public bool IsOpen { get; init; }
    public decimal Balance { get; init; }
    public int Deposits { get; init; }
    public decimal? OverdraftLimit { get; init; }
    public int? Tier { get; init; }
    public DateTimeOffset? LastValueDate { get; init; }

    internal AccountState Apply(AccountOpened e) =>
        this with { IsOpen = true, OverdraftLimit = e.OverdraftLimit, Tier = e.Tier };

    internal AccountState Apply(FundsDeposited e) =>
        this with { Balance = Balance + e.Amount, Deposits = Deposits + 1, LastValueDate = e.ValueDate };

    internal AccountState Apply(OverdraftLimitChanged e) => this with { OverdraftLimit = e.NewLimit };
}

public sealed partial class Account : Aggregate<AccountId, AccountState>
{
    public void Open(decimal? overdraftLimit, int? tier) => Raise(new AccountOpened(overdraftLimit, tier));

    public void Deposit(decimal amount, DateTimeOffset? valueDate) => Raise(new FundsDeposited(amount, valueDate));

    public void ChangeOverdraftLimit(decimal? newLimit) => Raise(new OverdraftLimitChanged(newLimit));
}

/// <summary>
/// Adds the retired <see cref="FundsDepositedV1"/> name to the generated
/// <see cref="AccountEventTypeRegistry"/>, the way an adopter keeps old streams readable.
/// </summary>
internal sealed class AccountRegistryWithLegacy : IEventTypeRegistry
{
    private readonly AccountEventTypeRegistry _generated = new();

    public bool TryGetType(string eventType, out Type? type)
    {
        if (string.Equals(eventType, nameof(FundsDepositedV1), StringComparison.Ordinal))
        {
            type = typeof(FundsDepositedV1);
            return true;
        }
        return _generated.TryGetType(eventType, out type);
    }

    public string GetTypeName(Type type) =>
        type == typeof(FundsDepositedV1) ? nameof(FundsDepositedV1) : _generated.GetTypeName(type);
}

/// <summary>
/// Hand-rolled binary serializer for the Account events. No System.Text.Json, so the publish
/// stays free of trim warnings. A nullable field is written as a presence byte and the value.
/// </summary>
internal sealed class AccountEventSerializer : IEventSerializer
{
    public ReadOnlyMemory<byte> Serialize<TEvent>(TEvent @event) where TEvent : notnull
    {
        var w = new List<byte>(64);
        switch (@event)
        {
            case AccountOpened e:
                WriteNullableDecimal(w, e.OverdraftLimit);
                WriteNullableInt(w, e.Tier);
                break;
            case FundsDeposited e:
                WriteDecimal(w, e.Amount);
                WriteNullableDateTimeOffset(w, e.ValueDate);
                break;
            case OverdraftLimitChanged e:
                WriteNullableDecimal(w, e.NewLimit);
                break;
            case FundsDepositedV1 e:
                WriteDecimal(w, e.Amount);
                break;
            default:
                throw new NotSupportedException($"Unsupported event type {@event.GetType().FullName}");
        }
        return w.ToArray();
    }

    public object Deserialize(ReadOnlyMemory<byte> payload, Type eventType)
    {
        var span = payload.Span;
        var offset = 0;
        if (eventType == typeof(AccountOpened))
            return new AccountOpened(ReadNullableDecimal(span, ref offset), ReadNullableInt(span, ref offset));
        if (eventType == typeof(FundsDeposited))
            return new FundsDeposited(ReadDecimal(span, ref offset), ReadNullableDateTimeOffset(span, ref offset));
        if (eventType == typeof(OverdraftLimitChanged))
            return new OverdraftLimitChanged(ReadNullableDecimal(span, ref offset));
        if (eventType == typeof(FundsDepositedV1))
            return new FundsDepositedV1(ReadDecimal(span, ref offset));
        throw new NotSupportedException($"Unsupported event type {eventType.FullName}");
    }

    private static void WriteInt(List<byte> w, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        foreach (var b in buf) w.Add(b);
    }

    private static void WriteLong(List<byte> w, long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        foreach (var b in buf) w.Add(b);
    }

    private static void WriteDecimal(List<byte> w, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        foreach (var part in bits) WriteInt(w, part);
    }

    private static void WriteNullableDecimal(List<byte> w, decimal? value)
    {
        w.Add(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue) WriteDecimal(w, value.Value);
    }

    private static void WriteNullableInt(List<byte> w, int? value)
    {
        w.Add(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue) WriteInt(w, value.Value);
    }

    private static void WriteNullableDateTimeOffset(List<byte> w, DateTimeOffset? value)
    {
        w.Add(value.HasValue ? (byte)1 : (byte)0);
        if (!value.HasValue) return;
        WriteLong(w, value.Value.Ticks);
        WriteInt(w, (int)value.Value.Offset.TotalMinutes);
    }

    private static int ReadInt(ReadOnlySpan<byte> s, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(s[offset..]);
        offset += 4;
        return value;
    }

    private static long ReadLong(ReadOnlySpan<byte> s, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(s[offset..]);
        offset += 8;
        return value;
    }

    private static decimal ReadDecimal(ReadOnlySpan<byte> s, ref int offset)
    {
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++) bits[i] = ReadInt(s, ref offset);
        return new decimal(bits);
    }

    private static decimal? ReadNullableDecimal(ReadOnlySpan<byte> s, ref int offset) =>
        s[offset++] == 0 ? null : ReadDecimal(s, ref offset);

    private static int? ReadNullableInt(ReadOnlySpan<byte> s, ref int offset) =>
        s[offset++] == 0 ? null : ReadInt(s, ref offset);

    private static DateTimeOffset? ReadNullableDateTimeOffset(ReadOnlySpan<byte> s, ref int offset)
    {
        if (s[offset++] == 0) return null;
        var ticks = ReadLong(s, ref offset);
        var minutes = ReadInt(s, ref offset);
        return new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
    }
}

#pragma warning restore MA0048

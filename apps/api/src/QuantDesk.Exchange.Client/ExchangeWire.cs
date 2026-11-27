using QuantDesk.Contracts.V1.Exchange;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace QuantDesk.Exchange.Client;

/// <summary>Exact wire-to-domain conversions for USD whole-share equity orders.
/// Does not authorize orders or enforce the market catalogue/tick size.</summary>
public static class ExchangeWire
{
    public const decimal PriceScale = 10_000m;
    public const long MaxUtcMicros = 253402300799999999L;

    public static string Id(Guid id) => id == Guid.Empty ? throw new ArgumentException("Empty UUID") : id.ToString("D");
    public static Guid ParseId(string? value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
            throw new ArgumentException("Expected a canonical nonzero lowercase UUID");
        return id;
    }
    public static void ValidateEpoch(Guid epoch)
    {
        var text = Id(epoch);
        if (text[14] != '4' || !"89ab".Contains(text[19])) throw new ArgumentException("Expected UUIDv4 journal epoch");
    }
    public static long ToPriceUnits(decimal price)
    {
        if (price <= 0 || price > long.MaxValue / PriceScale) throw new ArgumentOutOfRangeException(nameof(price));
        var units = price * PriceScale;
        if (units != decimal.Truncate(units)) throw new ArgumentException("Price is not exactly representable in 0.0001 USD units.", nameof(price));
        return checked((long)units);
    }
    public static decimal ToUsd(long units) => units > 0 ? units / PriceScale : throw new ArgumentOutOfRangeException(nameof(units));
    public static DateTimeOffset ToUtc(long micros) => micros is > 0 and <= MaxUtcMicros
        ? DateTimeOffset.UnixEpoch.AddTicks(checked(micros * 10)) : throw new ArgumentOutOfRangeException(nameof(micros));
    private static void ValidateNotional(long units, long quantity)
    {
        if (units <= 0 || quantity <= 0 || units > long.MaxValue / quantity)
            throw new ArgumentException("Price, quantity, or scaled notional outside native int64 range.");
    }
    public static void ValidateBook(BookKey? book)
    {
        if (book is null) throw new ArgumentException("Book is required.");
        ParseId(book.SessionId);
        if (book.InstrumentId.Length is < 1 or > 128 || book.InstrumentId.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Instrument must match the dev transport's printable ASCII identifier format.");
    }
    public static void ValidateOrder(EquityLimitOrder? order)
    {
        if (order is null) throw new ArgumentException("Order is required.");
        ValidateBook(order.Book); ParseId(order.AccountId); ParseId(order.OrderId);
        if (order.Side is not (Side.Buy or Side.Sell)) throw new ArgumentException("Invalid side.");
        ValidateNotional(order.LimitPriceUnits, order.Quantity);
    }

    public static EquityLimitOrder CreateOrder(Guid sessionId, string instrumentId, Guid orderId, Guid accountId, Side side, decimal limitPrice, long quantity)
    {
        var result = new EquityLimitOrder { Book = new BookKey { SessionId = Id(sessionId), InstrumentId = instrumentId },
            OrderId = Id(orderId), AccountId = Id(accountId), Side = side, LimitPriceUnits = ToPriceUnits(limitPrice), Quantity = quantity };
        ValidateOrder(result);
        return result;
    }

    // UUIDv5 name is 16 binary bytes (big endian command sequence, zero-based fill index), not text.
    private static Guid ExpectedFillId(Guid epoch, long sequence, int index)
    {
        Span<byte> input = stackalloc byte[32];
        epoch.ToByteArray(bigEndian: true).AsSpan().CopyTo(input);
        BinaryPrimitives.WriteInt64BigEndian(input[16..24], sequence);
        BinaryPrimitives.WriteInt64BigEndian(input[24..32], index);
        var hash = SHA1.HashData(input); // UUIDv5 naming, not a security primitive.
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0,16), bigEndian: true);
    }

    /// <summary>Check full page before applying any effects. Never advance a durable cursor here.</summary>
    public static void ValidateDelivery(DeliveryBatch batch, Guid epoch, long after, uint maxCommands)
    {
        ValidateEpoch(epoch);
        if (after < 0 || maxCommands is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(after));
        if (batch.JournalEpoch != Id(epoch) || batch.HighWatermark < after ||
            batch.Records.Count != Math.Min((long)maxCommands, batch.HighWatermark - after))
            throw new InvalidDataException("Wrong epoch, watermark, or incomplete page.");
        var seen = new HashSet<Guid>();
        var seq = after;
        foreach (var record in batch.Records)
        {
            if (record.CommandSequence != ++seq || !seen.Add(ParseId(record.CommandId)))
                throw new InvalidDataException("Gap or duplicate command in delivery.");
            switch (record.EventCase)
            {
                case DeliveryRecord.EventOneofCase.Registered:
                    ValidateBook(record.Registered.Book); break;
                case DeliveryRecord.EventOneofCase.Cancelled:
                    ValidateBook(record.Cancelled.Book); ParseId(record.Cancelled.OrderId); ParseId(record.Cancelled.AccountId); break;
                case DeliveryRecord.EventOneofCase.Submitted:
                    ValidateSubmission(record.Submitted, epoch, seq); break;
                default: throw new InvalidDataException("Unknown delivery event.");
            }
        }
    }

    private static void ValidateSubmission(SubmittedOrder submitted, Guid epoch, long sequence)
    {
        var order = submitted.Order;
        ValidateOrder(order);
        if (submitted.FilledQuantity < 0 || submitted.FilledQuantity > order.Quantity ||
            submitted.OpenQuantity != order.Quantity - submitted.FilledQuantity ||
            submitted.State != (submitted.FilledQuantity == 0 ? OrderState.Accepted :
                submitted.OpenQuantity == 0 ? OrderState.Filled : OrderState.PartiallyFilled))
            throw new InvalidDataException("Inconsistent original submission quantities/state.");
        long total = 0, previousBookSequence = 0, timestamp = 0;
        for (var i = 0; i < submitted.Fills.Count; i++)
        {
            var f = submitted.Fills[i];
            var mapped = MapFill(f);
            if (mapped.ExecutionId != ExpectedFillId(epoch, sequence, i) || !f.Book.Equals(order.Book) ||
                (i > 0 && (previousBookSequence == long.MaxValue || f.BookExecutionSequence != previousBookSequence + 1 || f.ExecutionTimeUtcUnixMicros != timestamp)) ||
                (order.Side == Side.Buy && (f.BuyOrderId != order.OrderId || f.BuyerAccountId != order.AccountId || f.ExecutionPriceUnits > order.LimitPriceUnits)) ||
                (order.Side == Side.Sell && (f.SellOrderId != order.OrderId || f.SellerAccountId != order.AccountId || f.ExecutionPriceUnits < order.LimitPriceUnits)) ||
                f.Quantity > submitted.FilledQuantity - total)
                throw new InvalidDataException("Invalid fill identity, sequence, timestamp, or order relationship.");
            total += f.Quantity; previousBookSequence = f.BookExecutionSequence; timestamp = f.ExecutionTimeUtcUnixMicros;
        }
        if (total != submitted.FilledQuantity) throw new InvalidDataException("Fill sum does not match submission.");
    }

    /// <summary>Normalize a validated delivery fill for later M07 mapping. This does not settle it.</summary>
    public static ExchangeFill MapFill(EquityFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ValidateBook(fill.Book); ValidateNotional(fill.ExecutionPriceUnits, fill.Quantity);
        var buy = ParseId(fill.BuyOrderId); var sell = ParseId(fill.SellOrderId);
        var buyer = ParseId(fill.BuyerAccountId); var seller = ParseId(fill.SellerAccountId);
        if (buy == sell || buyer == seller || fill.BookExecutionSequence <= 0) throw new ArgumentException("Invalid fill relationships.");
        return new ExchangeFill(ParseId(fill.ExecutionId), ParseId(fill.Book.SessionId), fill.Book.InstrumentId,
            buy, sell, buyer, seller, fill.Quantity, ToUsd(fill.ExecutionPriceUnits), ToUtc(fill.ExecutionTimeUtcUnixMicros), fill.BookExecutionSequence);
    }
}

public sealed record ExchangeFill(Guid ExecutionId, Guid SessionId, string InstrumentId,
    Guid BuyOrderId, Guid SellOrderId, Guid BuyerAccountId, Guid SellerAccountId,
    long Quantity, decimal ExecutionPrice, DateTimeOffset ExecutedAtUtc, long BookExecutionSequence);

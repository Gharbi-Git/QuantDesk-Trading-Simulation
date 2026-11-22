namespace QuantDesk.Modules.Portfolio.Domain.Entities;

public sealed class CompteTrading
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; private set; }

    public string AccountType { get; private set; } = "Personal";

    public string Currency { get; private set; } = "USD";

    public decimal CashBalance { get; private set; }

    // Explicit optimistic concurrency token; every settled cash mutation advances it.
    public long Version { get; private set; }

    public DateTime CreatedAtUtc { get; private set; } =
        DateTime.UtcNow;

    public Portefeuille Portefeuille { get; private set; } = null!;

    public List<CashJournalEntry> Journal { get; private set; } = new();

    private CompteTrading() { }

    /// <summary>
    /// Internal-only cash mutation for validated execution settlement. Never called
    /// directly from an HTTP request. The service writes a matching journal row
    /// in the same PostgreSQL transaction.
    /// </summary>
    internal void ApplySettlementCash(decimal signedAmount)
    {
        if (signedAmount == 0m || decimal.Round(signedAmount, 4) != signedAmount)
            throw new ArgumentOutOfRangeException(nameof(signedAmount));

        var nextBalance = checked(CashBalance + signedAmount);
        if (nextBalance < 0m)
            throw new InvalidOperationException("Insufficient available cash for execution.");

        CashBalance = nextBalance;
        Version = checked(Version + 1);
    }

    public static CompteTrading OpenPersonal(
        Guid ownerUserId,
        decimal startingCash)
    {
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("Owner is required.");

        if (startingCash <= 0 ||
            decimal.Round(startingCash, 2) != startingCash)
            throw new ArgumentOutOfRangeException(
                nameof(startingCash),
                "Starting cash must be positive and have at most two decimal places.");

        var account = new CompteTrading
        {
            OwnerUserId = ownerUserId,
            CashBalance = startingCash
        };

        account.Portefeuille = new Portefeuille(account.Id);

        account.Journal.Add(new CashJournalEntry(
            account.Id,
            startingCash,
            $"initial-funding:{account.Id:D}"));

        return account;
    }
}

public sealed class Portefeuille
{
    public Guid CompteTradingId { get; private set; }

    public CompteTrading CompteTrading { get; private set; } = null!;

    public List<PositionAction> Positions { get; private set; } = new();

    private Portefeuille() { }

    internal Portefeuille(Guid accountId)
    {
        CompteTradingId = accountId;
    }
}

public sealed class CashJournalEntry
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid CompteTradingId { get; private set; }

    public CompteTrading CompteTrading { get; private set; } = null!;

    public decimal Amount { get; private set; }

    public string EventKey { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; } =
        DateTime.UtcNow;

    private CashJournalEntry() { }

    internal CashJournalEntry(
        Guid accountId,
        decimal amount,
        string eventKey)
    {
        CompteTradingId = accountId;
        Amount = amount;
        EventKey = eventKey;
    }
}

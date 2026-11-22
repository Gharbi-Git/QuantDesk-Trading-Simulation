
using QuantDesk.Modules.Portfolio.Domain.Entities;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class TradingAccountDomainTests
{
    [Fact]
    public void Opening_personal_account_creates_portfolio()
    {
        var ownerId = Guid.NewGuid();

        var account = CompteTrading.OpenPersonal(
            ownerId,
            100_000m);

        Assert.Equal(ownerId, account.OwnerUserId);
        Assert.Equal("Personal", account.AccountType);
        Assert.Equal("USD", account.Currency);
        Assert.Equal(100_000m, account.CashBalance);

        Assert.NotNull(account.Portefeuille);
        Assert.Equal(
            account.Id,
            account.Portefeuille.CompteTradingId);
    }

    [Fact]
    public void Opening_account_creates_one_funding_entry()
    {
        var account = CompteTrading.OpenPersonal(
            Guid.NewGuid(),
            100_000m);

        var entry = Assert.Single(account.Journal);

        Assert.Equal(account.Id, entry.CompteTradingId);
        Assert.Equal(100_000m, entry.Amount);
        Assert.Equal(
            $"initial-funding:{account.Id:D}",
            entry.EventKey);
    }

    [Fact]
    public void Account_requires_valid_owner()
    {
        Assert.Throws<ArgumentException>(() =>
            CompteTrading.OpenPersonal(
                Guid.Empty,
                100_000m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(-0.01)]
    public void Account_rejects_nonpositive_funding(
        decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompteTrading.OpenPersonal(
                Guid.NewGuid(),
                amount));
    }

    [Fact]
    public void Account_rejects_more_than_two_decimal_places()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompteTrading.OpenPersonal(
                Guid.NewGuid(),
                100.001m));
    }
}

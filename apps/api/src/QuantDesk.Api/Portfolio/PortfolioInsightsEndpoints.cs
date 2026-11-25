using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;

namespace QuantDesk.Api.Portfolio;

/// <summary>Owner-only read views. No untrusted trade or mark mutation endpoints.</summary>
public static class PortfolioInsightsEndpoints
{
    public static void MapPortfolioInsights(this WebApplication app)
    {
        var group = app.MapGroup("/api/accounts/{id:guid}")
            .RequireAuthorization("PlayerOnly");

        // QD-054: Mark is the latest persisted, trusted stock execution involving this
        // account, NOT a live external quote or cross-session market price. If any open holding is unmarked/stale,
        // all-portfolio NAV and unrealized P&L are deliberately unavailable.
        group.MapGet("/valuation", async (
            Guid id, ClaimsPrincipal user, DbContextOptions<QuantDeskDbContext> options, CancellationToken ct) =>
        {
            if (!Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value, out var userId) || userId == Guid.Empty)
                return Results.Unauthorized();
            if (id == Guid.Empty) return Results.NotFound();
            var snapshot = await new PortfolioSnapshotReader(options).ReadOwnedAsync(userId, id, ct);
            if (snapshot is null) return Results.NotFound();

            return Results.Ok(new
            {
                snapshot.AccountId,
                snapshot.Currency,
                snapshot.AsOfUtc,
                snapshot.Version,
                snapshot.MarkPolicy,
                markDisclosure = "Indicative marks from your account's last settled trade within 24 hours; not live quotes or session-wide market prices. " +
                    "NAV is unavailable if any open holding lacks a recent mark.",
                snapshot.CashBalance,
                snapshot.ReservedCash,
                snapshot.AvailableCash,
                snapshot.TotalCostBasis,
                snapshot.RealizedPnl,
                snapshot.MarketValue,
                snapshot.Nav,
                snapshot.UnrealizedPnl,
                snapshot.MissingMarkInstruments,
                snapshot.Positions
            });
        });

        // QD-056: Return the latest 100 real cash events. Calculate the full
        // journal balance in SQL so both reconciliation and running balances
        // remain correct even when older events are not returned.
        group.MapGet("/balance-history", async (
            Guid id, ClaimsPrincipal user, QuantDeskDbContext db, CancellationToken ct) =>
        {
            var account = await OwnedAccountAsync(id, user, db, ct);
            if (account is null) return OwnershipFailure(user);

            const int maxHistoryEvents = 100;
            var journalRows = db.CashJournal.AsNoTracking()
                .Where(j => j.CompteTradingId == id);

            // Aggregated server-side; avoid loading the entire journal into memory.
            var journalBalance = await journalRows
                .SumAsync(j => (decimal?)j.Amount, ct) ?? 0m;

            // The additional row tells us whether any earlier history was omitted.
            var latestDescending = await journalRows
                .OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id)
                .Take(maxHistoryEvents + 1)
                .Select(j => new CashEvent(j.Id, j.Amount, j.EventKey, j.CreatedAtUtc))
                .ToListAsync(ct);

            var historyTruncated = latestDescending.Count > maxHistoryEvents;
            if (historyTruncated) latestDescending.RemoveAt(maxHistoryEvents);
            latestDescending.Reverse();

            // Running balances are absolute, not relative to the truncated window.
            decimal running = checked(journalBalance - latestDescending.Sum(j => j.Amount));
            var history = latestDescending.Select(j =>
            {
                running = checked(running + j.Amount);
                return new
                {
                    j.Id, j.Amount, j.EventKey, j.CreatedAtUtc,
                    balanceAfter = running
                };
            }).ToArray();

            return Results.Ok(new
            {
                accountId = id,
                currency = account.Currency,
                currentCashBalance = account.CashBalance,
                journalBalance,
                reconciles = journalBalance == account.CashBalance,
                historyTruncated,
                history
            });
        });

        // Actual buyer/seller executions only; never expose the counterparty identity.
        group.MapGet("/trades", async (
            Guid id, ClaimsPrincipal user, QuantDeskDbContext db, CancellationToken ct) =>
        {
            var account = await OwnedAccountAsync(id, user, db, ct);
            if (account is null) return OwnershipFailure(user);

            var trades = await db.EquitySettlements.AsNoTracking()
                .Where(e => e.BuyerAccountId == id || e.SellerAccountId == id)
                .OrderByDescending(e => e.SettledAtUtc)
                .ThenByDescending(e => e.ExecutionId)
                .Take(100)
                .Select(e => new
                {
                    e.ExecutionId,
                    e.InstrumentId,
                    side = e.BuyerAccountId == id ? "BUY" : "SELL",
                    e.Quantity,
                    e.ExecutionPrice,
                    notional = e.Quantity * e.ExecutionPrice,
                    e.SettledAtUtc
                })
                .ToListAsync(ct);
            return Results.Ok(trades);
        });
    }

    private static async Task<QuantDesk.Modules.Portfolio.Domain.Entities.CompteTrading?> OwnedAccountAsync(
        Guid id, ClaimsPrincipal user, QuantDeskDbContext db, CancellationToken ct)
    {
        if (!Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value, out var userId))
            return null;
        return await db.ComptesTrading.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.OwnerUserId == userId, ct);
    }

    private static IResult OwnershipFailure(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst("quantdesk_user_id")?.Value, out _)
            ? Results.NotFound() : Results.Unauthorized();

    private sealed record CashEvent(Guid Id, decimal Amount, string EventKey, DateTime CreatedAtUtc);
}

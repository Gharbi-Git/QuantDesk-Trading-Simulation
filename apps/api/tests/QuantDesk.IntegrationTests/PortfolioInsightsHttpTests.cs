using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using QuantDesk.Modules.Infrastructure.Persistence;
using QuantDesk.Modules.Portfolio.Domain.Entities;
using QuantDesk.Modules.Portfolio.Infrastructure.Services;
using Xunit;

namespace QuantDesk.IntegrationTests;

/// <summary>
/// Exercises the real HTTP endpoints and PostgreSQL-backed ownership checks.
/// All generated identities, accounts, orders, and settlement receipts are test-only.
/// </summary>
public sealed class PortfolioInsightsHttpTests
{
    private static readonly string[] Endpoints = ["valuation", "balance-history", "trades"];

    [Fact]
    public async Task Financial_endpoints_enforce_authentication_roles_and_ownership()
    {
        await using var fixture = await PortfolioHttpFixture.CreateAsync();

        foreach (var endpoint in Endpoints)
        {
            // No session -> 401. Admin and Session Organizer cannot use Player-only endpoints.
            Assert.Equal(HttpStatusCode.Unauthorized,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint));
            Assert.Equal(HttpStatusCode.Forbidden,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint, "ADMIN", fixture.OwnerUserId));
            Assert.Equal(HttpStatusCode.Forbidden,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint, "SUPERUSER", fixture.OwnerUserId));

            // A Player with a valid but non-owning identity receives 404, not financial data.
            Assert.Equal(HttpStatusCode.NotFound,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint, "PLAYER", fixture.OtherUserId));

            // A Player without the QuantDesk ID claim is not a valid portfolio owner.
            Assert.Equal(HttpStatusCode.Unauthorized,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint, "PLAYER"));

            // A Player with a matching owner ID can read their own account.
            Assert.Equal(HttpStatusCode.OK,
                await fixture.StatusAsync(fixture.OwnerAccountId, endpoint, "PLAYER", fixture.OwnerUserId));
        }
    }

    [Fact]
    public async Task Valuation_reports_cash_and_share_reservations_without_reducing_nav()
    {
        await using var fixture = await PortfolioHttpFixture.CreateAsync();
        await fixture.SeedTrustedStockFillsAsync(3);
        await fixture.ReserveAssetsAsync();
        using var response = await fixture.GetAsync(fixture.OwnerAccountId, "valuation", "PLAYER", fixture.OwnerUserId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal(99997m, root.GetProperty("cashBalance").GetDecimal());
        Assert.Equal(15000m, root.GetProperty("reservedCash").GetDecimal());
        Assert.Equal(84997m, root.GetProperty("availableCash").GetDecimal());
        Assert.Equal(100000m, root.GetProperty("nav").GetDecimal());
        Assert.Equal(5L, root.GetProperty("version").GetInt64());
        var position = Assert.Single(root.GetProperty("positions").EnumerateArray().ToArray());
        Assert.Equal(3L, position.GetProperty("quantity").GetInt64());
        Assert.Equal(2L, position.GetProperty("reservedShares").GetInt64());
        Assert.Equal(1L, position.GetProperty("availableShares").GetInt64());
        using var denied = await fixture.GetAsync(fixture.OwnerAccountId, "valuation", "PLAYER", fixture.OtherUserId);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task Fresh_account_has_one_funding_event_and_reconciled_history()
    {
        await using var fixture = await PortfolioHttpFixture.CreateAsync();
        using var response = await fixture.GetAsync(
            fixture.OwnerAccountId, "balance-history", "PLAYER", fixture.OwnerUserId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.False(root.GetProperty("historyTruncated").GetBoolean());
        Assert.True(root.GetProperty("reconciles").GetBoolean());
        Assert.Equal(100_000m, root.GetProperty("journalBalance").GetDecimal());
        var first = Assert.Single(root.GetProperty("history").EnumerateArray().ToArray());
        Assert.Equal(100_000m, first.GetProperty("balanceAfter").GetDecimal());
        Assert.StartsWith("initial-funding:", first.GetProperty("eventKey").GetString());
    }

    [Fact]
    public async Task Cash_history_is_bounded_but_uses_absolute_running_balances()
    {
        await using var fixture = await PortfolioHttpFixture.CreateAsync();

        // Seed shares through the same fixture-only domain path as settlement tests.
        // 100 trusted fills create 100 debit journal rows plus the initial funding row.
        await fixture.SeedTrustedStockFillsAsync(100);

        using var response = await fixture.GetAsync(
            fixture.OwnerAccountId, "balance-history", "PLAYER", fixture.OwnerUserId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("historyTruncated").GetBoolean());
        Assert.True(root.GetProperty("reconciles").GetBoolean());
        Assert.Equal(99_900m, root.GetProperty("currentCashBalance").GetDecimal());
        Assert.Equal(99_900m, root.GetProperty("journalBalance").GetDecimal());

        var history = root.GetProperty("history");
        Assert.Equal(100, history.GetArrayLength());
        Assert.Equal(99_999m, history[0].GetProperty("balanceAfter").GetDecimal());
        Assert.Equal(99_900m, history[99].GetProperty("balanceAfter").GetDecimal());
        Assert.All(history.EnumerateArray().ToArray(),
            entry => Assert.Equal(-1m, entry.GetProperty("amount").GetDecimal()));
    }
}

/// <summary>Dedicated test host; no production auth or database configuration is changed.</summary>
internal sealed class PortfolioHttpTestFactory(string testConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuantDesk"] = testConnectionString,
                ["Keycloak:Authority"] = "http://127.0.0.1:8180/realms/quantdesk",
                ["Keycloak:ClientId"] = "quantdesk-bff",
                ["Keycloak:ClientSecret"] = "test-only-placeholder"
            }));

        builder.ConfigureTestServices(services =>
        {
            // The application's DbContext registration captures its connection string
            // during Program startup. The test host must explicitly replace that
            // registration so HTTP requests and fixture seeding use the same TEST DB.
            services.RemoveAll<DbContextOptions<QuantDeskDbContext>>();
            services.RemoveAll<QuantDeskDbContext>();
            services.AddDbContext<QuantDeskDbContext>(options =>
                options.UseNpgsql(testConnectionString));

            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, PortfolioFakeAuthenticationHandler>(
                    "PortfolioTestAuth", _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = "PortfolioTestAuth";
                options.DefaultAuthenticateScheme = "PortfolioTestAuth";
                options.DefaultChallengeScheme = "PortfolioTestAuth";
                options.DefaultForbidScheme = "PortfolioTestAuth";
            });
        });
    }
}

/// <summary>Tests use request headers to simulate actors; these headers do not exist in production.</summary>
internal sealed class PortfolioFakeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Portfolio-Test-Role", out var role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Portfolio HTTP Test"),
            new(ClaimTypes.Role, role.ToString())
        };
        if (Request.Headers.TryGetValue("X-Portfolio-Test-User", out var id) &&
            Guid.TryParse(id.ToString(), out var userId))
            claims.Add(new Claim("quantdesk_user_id", userId.ToString("D")));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "PortfolioTestAuth"));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, "PortfolioTestAuth")));
    }
}

internal sealed class PortfolioHttpFixture : IAsyncDisposable
{
    private readonly DbContextOptions<QuantDeskDbContext> _options;
    private readonly PortfolioHttpTestFactory _factory;
    private readonly HttpClient _client;
    public Guid OwnerUserId { get; } = Guid.NewGuid();
    public Guid OtherUserId { get; } = Guid.NewGuid();
    public Guid OwnerAccountId { get; private set; }
    public Guid OtherAccountId { get; private set; }
    private readonly string _instrument = "EQ-" + Guid.NewGuid().ToString("N");

    private PortfolioHttpFixture(string connectionString)
    {
        _options = new DbContextOptionsBuilder<QuantDeskDbContext>()
            .UseNpgsql(connectionString).Options;
        _factory = new PortfolioHttpTestFactory(connectionString);
        _client = _factory.CreateClient();
    }

    public static async Task<PortfolioHttpFixture> CreateAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("QD_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException("QD_TEST_CONNECTION_STRING is required.");
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(parsed.Database, "quantdesk_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Portfolio HTTP tests may only use the quantdesk_test database.");

        var fixture = new PortfolioHttpFixture(parsed.ConnectionString);
        try
        {
            await fixture.SeedAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private QuantDeskDbContext OpenDb() => new(_options);

    private async Task SeedAsync()
    {
        await using var db = OpenDb();
        await db.Database.MigrateAsync();

        foreach (var id in new[] { OwnerUserId, OtherUserId })
        {
            var unique = id.ToString("N");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO utilisateurs (id, keycloak_subject, username, email, created_at_utc) VALUES ({id}, {"portfolio-http-" + unique}, {"portfolio_" + unique}, {"portfolio@example.invalid"}, {DateTime.UtcNow})");
        }

        var ownerAccount = CompteTrading.OpenPersonal(OwnerUserId, 100_000m);
        var otherAccount = CompteTrading.OpenPersonal(OtherUserId, 100_000m);
        OwnerAccountId = ownerAccount.Id;
        OtherAccountId = otherAccount.Id;
        db.ComptesTrading.AddRange(ownerAccount, otherAccount);
        await db.SaveChangesAsync();

        // Catch any future test-host DB configuration regression at setup time,
        // instead of reporting a misleading 404 from all three owner endpoints.
        using var scope = _factory.Services.CreateScope();
        var hostDb = scope.ServiceProvider.GetRequiredService<QuantDeskDbContext>();
        var hostDatabase = hostDb.Database.GetDbConnection().Database;
        if (!string.Equals(hostDatabase, "quantdesk_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Portfolio HTTP test host is targeting database '{hostDatabase}', not quantdesk_test.");
        var ownerVisible = await hostDb.ComptesTrading.AsNoTracking().AnyAsync(
            account => account.Id == OwnerAccountId && account.OwnerUserId == OwnerUserId);
        if (!ownerVisible)
            throw new InvalidOperationException(
                "Portfolio HTTP test host cannot see its seeded owner account. Check DbContext registration.");
    }

    public async Task SeedTrustedStockFillsAsync(int count)
    {
        await using (var db = OpenDb())
        {
            db.PositionsAction.Add(PositionAction.OpenWithBuy(OtherAccountId, _instrument, count, 1m));
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < count; i++)
        {
            await using var db = OpenDb();
            var execution = new TrustedEquityExecution(
                Guid.NewGuid(), OwnerAccountId, OtherAccountId, _instrument, 1, 1m);
            var result = await new EquityTradeSettlementService(db).SettleTrustedFillAsync(execution);
            Assert.False(result.AlreadySettled);
        }
    }

    public async Task ReserveAssetsAsync()
    {
        var service = new PortfolioReservationService(_options);
        var session = Guid.NewGuid();
        await service.ReserveAsync(OwnerUserId, Guid.NewGuid(), OwnerAccountId,
            session, _instrument, EquityReservationSide.Buy, 100, 150m);
        await service.ReserveAsync(OwnerUserId, Guid.NewGuid(), OwnerAccountId,
            session, _instrument, EquityReservationSide.Sell, 2, 1m);
    }

    public async Task<HttpStatusCode> StatusAsync(Guid account, string endpoint, string? role = null, Guid? userId = null)
    {
        using var response = await GetAsync(account, endpoint, role, userId);
        return response.StatusCode;
    }

    public async Task<HttpResponseMessage> GetAsync(Guid account, string endpoint, string? role, Guid? userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/accounts/{account:D}/{endpoint}");
        if (role is not null) request.Headers.Add("X-Portfolio-Test-Role", role);
        if (userId is not null) request.Headers.Add("X-Portfolio-Test-User", userId.Value.ToString("D"));
        return await _client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        // Receipts have RESTRICT account FKs. Other account-owned data cascades.
        await using (var db = OpenDb())
        {
            if (OwnerAccountId != Guid.Empty && OtherAccountId != Guid.Empty)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM equity_trade_settlements WHERE buyer_account_id = {OwnerAccountId} OR seller_account_id = {OwnerAccountId} OR buyer_account_id = {OtherAccountId} OR seller_account_id = {OtherAccountId}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM equity_order_reservations WHERE account_id = {OwnerAccountId} OR account_id = {OtherAccountId}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM comptes_trading WHERE \"Id\" = {OwnerAccountId} OR \"Id\" = {OtherAccountId}");
            }
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM utilisateurs WHERE id = {OwnerUserId} OR id = {OtherUserId}");
        }
        _client.Dispose();
        _factory.Dispose();
    }
}

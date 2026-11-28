using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using QuantDesk.Exchange.Outcomes.Client;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Exchange.Operational.Hosting;

public sealed record RecoveryHealth(string State, DateTimeOffset? LastSuccessfulPassUtc = null,
    string? FailureType = null);

// Candidate for the ASP.NET Core hosting boundary. Test database only.
// Session lease excludes cooperating hosts; it is NOT a distributed fencing token.
public sealed class OperationalHostedRecovery : BackgroundService
{
    private readonly OutcomeExchangeClient exchange;
    private readonly string connectionString;
    private readonly OperationalRecoveryWorker worker;
    private NpgsqlConnection? lease;
    private RecoveryHealth health = new("NotStarted");
    public RecoveryHealth Health => Volatile.Read(ref health);
    private void Publish(RecoveryHealth next) => Volatile.Write(ref health, next);
    internal OperationalHostedRecovery(OutcomeExchangeClient exchange, string raw, bool resumePreparedTestCommands)
    {
        this.exchange = exchange;
        var settings = new NpgsqlConnectionStringBuilder(raw);
        if (settings.Database != "quantdesk_test") throw new InvalidOperationException("Hosted candidate requires quantdesk_test.");
        var db = new DbContextOptionsBuilder<QuantDeskDbContext>().UseNpgsql(raw).Options;
        settings.Pooling = false; settings.Multiplexing = false;
        settings.Timeout = 10; settings.CommandTimeout = 10;
        connectionString = settings.ConnectionString;
        worker = new OperationalRecoveryWorker(new OperationalTradingFlow(exchange, db), resumePreparedTestCommands, 1);
    }
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Health.State != "NotStarted") throw new InvalidOperationException("Recovery host cannot be restarted in place.");
        Publish(new("Starting"));
        try
        {
            lease = new NpgsqlConnection(connectionString);
            await lease.OpenAsync(cancellationToken);
            await using (var q = new NpgsqlCommand("SELECT pg_try_advisory_lock(6061204::bigint)", lease))
                if (await q.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("Another operational recovery host owns the lease.");
            // Explicit schema and journal binding are required. Never provision here.
            var status = await exchange.GetStatusAsync(cancellationToken);
            await using (var tx = await lease.BeginTransactionAsync(cancellationToken))
            {
                await using (var q = new NpgsqlCommand("SELECT version FROM qd_exchange_v4.schema_version WHERE singleton", lease, tx))
                    if (await q.ExecuteScalarAsync(cancellationToken) is not int version || version != 1)
                        throw new InvalidOperationException("Unsupported operational schema.");
                var store = new OperationalCommandStore(lease, tx);
                await store.ObserveAsync(status, cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            await base.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Publish(new("Faulted", FailureType: ex.GetType().Name));
            await ReleaseLeaseAsync();
            throw;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // The lease connection is never re-opened automatically. Loss stops the host.
                var c = lease ?? throw new InvalidOperationException("Recovery lease missing.");
                await using (var q = new NpgsqlCommand("SELECT 1", c))
                    await q.ExecuteScalarAsync(stoppingToken);
                await worker.RunPassAsync(stoppingToken);
                Publish(new("Running", DateTimeOffset.UtcNow));
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Publish(new("Faulted", Health.LastSuccessfulPassUtc, ex.GetType().Name));
            // Do not expose SQL payloads or connection credentials in the host log.
            throw new InvalidOperationException("Operational recovery stopped on a failed pass: " + ex.GetType().Name);
        }
        finally
        {
            try { await ReleaseLeaseAsync(); }
            finally { if (Health.State != "Faulted") Publish(new("Stopped", Health.LastSuccessfulPassUtc)); }
        }
    }
    private async Task ReleaseLeaseAsync()
    {
        var c = Interlocked.Exchange(ref lease, null);
        if (c is not null) await c.DisposeAsync();
    }
}

public static class OperationalHostingRegistration
{
    // Must be explicitly enabled by a trusted test composition. No runtime DDL,
    // no player endpoints, no automatic risk admission. Client remains caller-owned.
    public static IServiceCollection AddOperationalRecoveryCandidate(this IServiceCollection services,
        OutcomeExchangeClient exchange, string connectionString, bool enabled = false,
        bool resumePreparedTestCommands = false)
    {
        if (!enabled) return services;
        if (services.Any(x => x.ServiceType == typeof(OperationalHostedRecovery)))
            throw new InvalidOperationException("Operational recovery already registered.");
        if (new NpgsqlConnectionStringBuilder(connectionString).Database != "quantdesk_test")
            throw new InvalidOperationException("Hosted candidate requires quantdesk_test.");
        services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        services.AddSingleton(_ => new OperationalHostedRecovery(exchange, connectionString, resumePreparedTestCommands));
        services.AddHostedService<OperationalHostedRecovery>(sp => sp.GetRequiredService<OperationalHostedRecovery>());
        services.AddHealthChecks().AddCheck<OperationalRecoveryHealthCheck>("exchange-operational-recovery");
        return services;
    }
}

public sealed class OperationalRecoveryHealthCheck(OperationalHostedRecovery worker) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = worker.Health;
        var ready = status.State == "Running" && status.LastSuccessfulPassUtc is { } last &&
            DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(30);
        return Task.FromResult(ready ? HealthCheckResult.Healthy("Recovery pass completed") :
            HealthCheckResult.Unhealthy("Recovery " + status.State + "; no recent successful pass"));
    }
}

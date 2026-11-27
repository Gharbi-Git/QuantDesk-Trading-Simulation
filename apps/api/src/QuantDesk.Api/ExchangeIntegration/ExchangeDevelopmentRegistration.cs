using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuantDesk.Exchange.Client;
using QuantDesk.Modules.Infrastructure.Persistence;

namespace QuantDesk.Api.ExchangeIntegration;

public static class ExchangeDevelopmentRegistration
{
    public static IServiceCollection AddDevelopmentExchange(this IServiceCollection services,
        IConfiguration config, IHostEnvironment environment, string databaseConnectionString)
    {
        if (!config.GetValue<bool>("ExchangeDevelopment:Enabled")) return services;
        if (!environment.IsDevelopment() || new NpgsqlConnectionStringBuilder(databaseConnectionString).Database != "quantdesk_test")
            throw new InvalidOperationException("Development exchange can only be enabled in Development with quantdesk_test.");
        if (!Guid.TryParseExact(config["ExchangeDevelopment:JournalEpoch"], "D", out var epoch))
            throw new InvalidOperationException("Explicit journal epoch required.");
        ExchangeWire.ValidateEpoch(epoch);
        var port = config.GetValue<int>("ExchangeDevelopment:Port");
        if (port is < 1 or > 65535) throw new InvalidOperationException("Explicit loopback port required.");
        services.AddSingleton<IExchangeClient>(_ => new GrpcExchangeClient(port, epoch));
        services.AddScoped<ExchangeDeliveryConsumer>();
        // Intentionally no hosted service, HTTP endpoint, or command dispatcher.
        return services;
    }
}

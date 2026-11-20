
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace QuantDesk.IntegrationTests;

public sealed class AuthorizationHttpTests
{
    [Theory]
    [InlineData(null, "/api/me", 401)]
    [InlineData(null, "/api/player/test", 401)]
    [InlineData(null, "/api/admin/test", 401)]
    [InlineData("PLAYER", "/api/me", 200)]
    [InlineData("PLAYER", "/api/player/test", 200)]
    [InlineData("PLAYER", "/api/admin/test", 403)]
    [InlineData("ADMIN", "/api/admin/test", 200)]
    public async Task Api_EnforcesAuthorization(
        string? role,
        string path,
        int expectedStatus)
    {
        using var factory = new AuthorizationTestFactory();
        using var client = factory.CreateClient();

        if (role is not null)
        {
            client.DefaultRequestHeaders.Add(
                "X-Test-Role", role);
        }

        using var response = await client.GetAsync(path);

        Assert.Equal(
            (HttpStatusCode)expectedStatus,
            response.StatusCode);
    }
}

public sealed class AuthorizationTestFactory
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:QuantDesk"] =
                        "Host=127.0.0.1;Database=unused_test;Username=test;Password=test",
                    ["Keycloak:Authority"] =
                        "http://127.0.0.1:8180/realms/quantdesk",
                    ["Keycloak:ClientId"] = "quantdesk-bff",
                    ["Keycloak:ClientSecret"] = "test-only-placeholder"
                });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<
                    AuthenticationSchemeOptions,
                    FakeAuthenticationHandler>(
                    "TestAuth", _ => { });

            services.PostConfigure<AuthenticationOptions>(
                options =>
                {
                    options.DefaultScheme = "TestAuth";
                    options.DefaultAuthenticateScheme = "TestAuth";
                    options.DefaultChallengeScheme = "TestAuth";
                    options.DefaultForbidScheme = "TestAuth";
                });
        });
    }
}

public sealed class FakeAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public FakeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult>
        HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(
                "X-Test-Role", out var role))
        {
            return Task.FromResult(
                AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim(ClaimTypes.Role, role.ToString()),
            new Claim(
                "quantdesk_user_id",
                Guid.Parse(
                    "11111111-1111-1111-1111-111111111111"
                ).ToString())
        };

        var identity = new ClaimsIdentity(
            claims, "TestAuth");

        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(
                    principal, "TestAuth")));
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace WebhookFx.Tests;

/// <summary>
/// One Postgres container and one in-process WebhookFx host for the whole test run.
/// The app is the real Program; only the connection string and the caller's IP are supplied by the test.
/// </summary>
public sealed class PipelineFixture : IAsyncLifetime
{
    /// <summary>First address in IpWhitelistMiddleware's allow list.</summary>
    public const string TradingViewIp = "52.89.214.238";
    public const string UnknownIp = "203.0.113.7";
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Program reads the connection string from configuration right after Build();
        // an environment variable is the one source that is guaranteed to be there by then.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddTransient<IStartupFilter, RemoteIpStartupFilter>()));

        // Force the host to start so the schema exists before the first test touches the database.
        _factory.CreateClient().Dispose();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>An HTTP client whose requests appear to come from <paramref name="remoteIp"/>.</summary>
    public HttpClient CreateClient(string remoteIp = TradingViewIp)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        return client;
    }

    public async Task ResetAsync()
    {
        await using var conn = await OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """TRUNCATE "OutboxEvents", "Signals", "Requests" RESTART IDENTITY CASCADE""";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// TestServer has no socket, so RemoteIpAddress is null. This filter runs before the
    /// application's own middleware and fills it in from a test-only header.
    /// </summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip))
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                await nextMiddleware();
            });
            next(app);
        };
    }
}

[CollectionDefinition(Name)]
public sealed class PipelineCollection : ICollectionFixture<PipelineFixture>
{
    public const string Name = "pipeline";
}

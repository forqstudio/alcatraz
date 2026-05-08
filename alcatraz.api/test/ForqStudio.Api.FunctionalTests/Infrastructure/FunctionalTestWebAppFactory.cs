using ForqStudio.Api.FunctionalTests.Users;
using ForqStudio.Application.Abstractions.Data;
using ForqStudio.Infrastructure;
using ForqStudio.Infrastructure.Authentication;
using ForqStudio.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ForqStudio.Api.FunctionalTests.Infrastructure;

public class FunctionalTestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithImage("postgres:latest")
        .WithDatabase("forqstudio")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redisContainer = new RedisBuilder()
        .WithImage("redis:latest")
        .Build();

    private readonly KeycloakContainer _keycloakContainer = new KeycloakBuilder()
        .WithResourceMapping(
            new FileInfo(".files/forqstudio-realm-export.json"),
            new FileInfo("/opt/keycloak/data/import/realm.json"))
        .WithCommand("--import-realm")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<ApplicationDbContext>));

            services.AddDbContext<ApplicationDbContext>(options =>
                options
                    .UseNpgsql(_dbContainer.GetConnectionString())
                    .UseSnakeCaseNamingConvention());

            services.RemoveAll(typeof(ISqlConnectionFactory));

            services.AddSingleton<ISqlConnectionFactory>(_ =>
                new SqlConnectionFactory(_dbContainer.GetConnectionString()));

            services.Configure<RedisCacheOptions>(redisCacheOptions =>
                redisCacheOptions.Configuration = _redisContainer.GetConnectionString());

            services.RemoveAll(typeof(IConnectionMultiplexer));
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString()));

            var keycloakAddress = _keycloakContainer.GetBaseAddress();

            services.Configure<KeycloakOptions>(o =>
            {
                o.AdminUrl = $"{keycloakAddress}admin/realms/forqstudio/";
                o.TokenUrl = $"{keycloakAddress}realms/forqstudio/protocol/openid-connect/token";
            });

            services.Configure<AuthenticationOptions>(o =>
            {
                o.Issuer = $"{keycloakAddress}realms/forqstudio/";
                o.MetadataUrl = $"{keycloakAddress}realms/forqstudio/.well-known/openid-configuration";
            });
        });
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await _redisContainer.StartAsync();
        await _keycloakContainer.StartAsync();

        await InitializeTestUserAsync();
    }

    public new async Task DisposeAsync()
    {
        await _dbContainer.StopAsync();
        await _redisContainer.StopAsync();
        await _keycloakContainer.StopAsync();
    }

    private async Task InitializeTestUserAsync()
    {
        var httpClient = CreateClient();

        await httpClient.PostAsJsonAsync("api/v1/users/register", UserData.RegisterTestUserRequest);
    }
}

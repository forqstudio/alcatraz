using Asp.Versioning;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Caching;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.Abstractions.Email;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Application.Abstractions.Security;
using Alcatraz.Application.Sandboxes.IssueSshCertificate;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Apartments;
using Alcatraz.Domain.Bookings;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Users;
using Alcatraz.Infrastructure.Authentication;
using Alcatraz.Infrastructure.Authorization;
using Alcatraz.Infrastructure.Caching;
using Alcatraz.Infrastructure.Clock;
using Alcatraz.Infrastructure.Data;
using Alcatraz.Infrastructure.Email;
using Alcatraz.Infrastructure.Messaging;
using Alcatraz.Infrastructure.Outbox;
using Alcatraz.Infrastructure.Repositories;
using Alcatraz.Infrastructure.Security;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Quartz;
using StackExchange.Redis;
using AuthenticationOptions = Alcatraz.Infrastructure.Authentication.AuthenticationOptions;
using AuthenticationService = Alcatraz.Infrastructure.Authentication.AuthenticationService;
using IAuthenticationService = Alcatraz.Application.Abstractions.Authentication.IAuthenticationService;

namespace Alcatraz.Infrastructure;

public static class DependencyInjection
{
    private const string DatabaseConnectionName = "Database";
    private const string CacheConnectionName = "Cache";
    private const string AuthenticationSection = "Authentication";
    private const string KeycloakSection = "Keycloak";
    private const string KeycloakBaseUrlKey = "KeyCloak:BaseUrl";
    private const string OutboxSection = "Outbox";
    private const string NatsSection = "Nats";
    private const string GatewaySection = "Gateway";
    private const string SshCaSection = "Ssh:CA";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddTransient<IDateTimeProvider, DateTimeProvider>();

        services.AddTransient<IEmailService, EmailService>();

        AddPersistence(services, configuration);

        AddAuthentication(services, configuration);

        AddAuthorization(services);

        AddCaching(services, configuration);

        AddHealthChecks(services, configuration);

        AddApiVersioning(services);

        AddBackgroundJobs(services, configuration);

        AddSandboxIntegrations(services, configuration);

        return services;
    }

    private static void AddSandboxIntegrations(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NatsOptions>(configuration.GetSection(NatsSection));

        services.Configure<GatewayOptions>(configuration.GetSection(GatewaySection));

        services.Configure<SshCertificateAuthorityOptions>(configuration.GetSection(SshCaSection));

        services.AddSingleton<NatsConnectionFactory>();

        services.AddSingleton<ISandboxEventPublisher, NatsSandboxEventPublisher>();

        services.AddSingleton<ISshCertificateAuthority, SshKeygenCertificateAuthority>();

        services.AddHostedService<VmReadyConsumer>();

        services.AddHostedService<VmDestroyedConsumer>();
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString =
            configuration.GetConnectionString(DatabaseConnectionName) ??
            throw new ArgumentNullException(nameof(configuration));

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IUserRepository, UserRepository>();

        services.AddScoped<IApartmentRepository, ApartmentRepository>();

        services.AddScoped<IBookingRepository, BookingRepository>();

        services.AddScoped<ISandboxRepository, SandboxRepository>();

        services.AddScoped<IPermissionRepository, PermissionRepository>();

        services.AddScoped<IRoleRepository, RoleRepository>();

        services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddSingleton<ISqlConnectionFactory>(_ =>
            new SqlConnectionFactory(connectionString));

        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.Configure<AuthenticationOptions>(configuration.GetSection(AuthenticationSection));

        services.ConfigureOptions<JwtBearerOptionsSetup>();

        services.Configure<KeycloakOptions>(configuration.GetSection(KeycloakSection));

        services.AddTransient<AdminAuthorizationDelegatingHandler>();

        services.AddHttpClient<IAuthenticationService, AuthenticationService>((serviceProvider, httpClient) =>
        {
            var keycloakOptions = serviceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;

            httpClient.BaseAddress = new Uri(keycloakOptions.AdminUrl);
        })
            .AddHttpMessageHandler<AdminAuthorizationDelegatingHandler>();

        services.AddHttpClient<IJwtService, JwtService>((serviceProvider, httpClient) =>
        {
            var keycloakOptions = serviceProvider.GetRequiredService<IOptions<KeycloakOptions>>().Value;

            httpClient.BaseAddress = new Uri(keycloakOptions.TokenUrl);
        });

        services.AddHttpClient<IDeviceAuthorizationClient, KeycloakDeviceAuthorizationClient>();

        services.AddHttpContextAccessor();

        services.AddScoped<IUserContext, UserContext>();
    }

    private static void AddAuthorization(IServiceCollection services)
    {
        services.AddScoped<AuthorizationService>();

        services.AddTransient<IClaimsTransformation, CustomClaimsTransformation>();

        services.AddTransient<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddTransient<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
    }

    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(CacheConnectionName) ??
                               throw new ArgumentNullException(nameof(configuration));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));

        services.AddStackExchangeRedisCache(options => options.Configuration = connectionString);

        services.AddSingleton<ICacheService, CacheService>();
    }

    private static void AddHealthChecks(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString(DatabaseConnectionName)!)
            .AddRedis(configuration.GetConnectionString(CacheConnectionName)!)
            .AddUrlGroup(new Uri(configuration[KeycloakBaseUrlKey]!), HttpMethod.Get, "keycloak");
    }

    private static void AddApiVersioning(IServiceCollection services)
    {
        services
            .AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1);
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
            });
    }

    private static void AddBackgroundJobs(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxSection));

        services.AddQuartz(c => {
            var scheduler = Guid.NewGuid();
            c.SchedulerId = $"default-id-{scheduler}";
            c.SchedulerName = $"default-name-{scheduler}";
        });

        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

        services.ConfigureOptions<ProcessOutboxMessagesJobSetup>();
    }
}
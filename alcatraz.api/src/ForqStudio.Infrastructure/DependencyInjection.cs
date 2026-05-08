using Asp.Versioning;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Caching;
using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Data;
using ForqStudio.Application.Abstractions.Email;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Apartments;
using ForqStudio.Domain.Bookings;
using ForqStudio.Domain.Users;
using ForqStudio.Infrastructure.Authentication;
using ForqStudio.Infrastructure.Authorization;
using ForqStudio.Infrastructure.Caching;
using ForqStudio.Infrastructure.Clock;
using ForqStudio.Infrastructure.Data;
using ForqStudio.Infrastructure.Email;
using ForqStudio.Infrastructure.Outbox;
using ForqStudio.Infrastructure.Repositories;
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
using AuthenticationOptions = ForqStudio.Infrastructure.Authentication.AuthenticationOptions;
using AuthenticationService = ForqStudio.Infrastructure.Authentication.AuthenticationService;
using IAuthenticationService = ForqStudio.Application.Abstractions.Authentication.IAuthenticationService;

namespace ForqStudio.Infrastructure;

public static class DependencyInjection
{
    private const string DatabaseConnectionName = "Database";
    private const string CacheConnectionName = "Cache";
    private const string AuthenticationSection = "Authentication";
    private const string KeycloakSection = "Keycloak";
    private const string KeycloakBaseUrlKey = "KeyCloak:BaseUrl";
    private const string OutboxSection = "Outbox";

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

        return services;
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
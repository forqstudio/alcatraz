using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Authentication;
using Alcatraz.Cli.Common.Cli;
using Alcatraz.Cli.Common.Configuration;
using Alcatraz.Cli.Common.Ssh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Alcatraz.Cli.Common.Bootstrap;

internal static class DependencyInjection
{
    public static IServiceCollection AddAlcatrazCli(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CancellationContext>();

        services.Configure<CliOptions>(configuration.GetSection("Cli"));

        services.AddSingleton<ICliConfigStore, CliConfigStore>();
        services.AddSingleton<ITokenStore, TokenStore>();
        services.AddSingleton<ISshKeyManager, SshKeyManager>();
        services.AddSingleton<ICertificateCache, CertificateCache>();
        services.AddSingleton<Commands.Ssh.ISshLauncher, Commands.Ssh.SshLauncher>();
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<IDeviceFlowOrchestrator, DeviceFlowOrchestrator>();
        services.AddSingleton<CommandRunner>();

        services.AddTransient<BearerHandler>();

        services.AddHttpClient<IAlcatrazApiClient, AlcatrazApiClient>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<CliOptions>>().Value;
            http.BaseAddress = new Uri(opt.ApiBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("alcatraz-cli/0.1");
        })
        .AddHttpMessageHandler<BearerHandler>();

        return services;
    }
}

using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Commands.Logout;
using Alcatraz.Cli.Commands.Sandboxes.CreateSandbox;
using Alcatraz.Cli.Commands.Sandboxes.DeleteSandbox;
using Alcatraz.Cli.Commands.Sandboxes.GetSandbox;
using Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;
using Alcatraz.Cli.Commands.Sandboxes.ListSandboxes;
using Alcatraz.Cli.Commands.Ssh;
using Alcatraz.Cli.Commands.WhoAmI;
using Alcatraz.Cli.Common.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Common.Bootstrap;

public static class CliBootstrap
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintBanner();
        }

        var configuration = BuildConfiguration(args);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAlcatrazCli(configuration);

        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("alcatraz");
            config.SetApplicationVersion("0.1.0");

            config.AddCommand<LoginCommand>("login")
                .WithDescription("Sign in via OAuth device flow.");
            config.AddCommand<LogoutCommand>("logout")
                .WithDescription("Forget cached tokens.");
            config.AddCommand<WhoAmICommand>("whoami")
                .WithDescription("Show the signed-in user.");

            config.AddBranch("sandbox", b =>
            {
                b.SetDescription("Manage sandboxes.");
                b.AddCommand<CreateSandboxCommand>("create").WithDescription("Create a new sandbox.");
                b.AddCommand<ListSandboxesCommand>("list").WithDescription("List your sandboxes.");
                b.AddCommand<GetSandboxCommand>("get").WithDescription("Show one sandbox.");
                b.AddCommand<DeleteSandboxCommand>("delete").WithDescription("Mark a sandbox for deletion.");
                b.AddCommand<IssueSshCertificateCommand>("ssh-cert").WithDescription("Issue an SSH cert for a sandbox.");
            });

            config.AddCommand<SshCommand>("ssh")
                .WithDescription("SSH into a sandbox.");
        });

        return await app.RunAsync(args);
    }

    private static void PrintBanner()
    {
        // Skip when output is redirected — keeps `alcatraz | jq`, CI logs, etc. clean.
        if (Console.IsOutputRedirected)
        {
            return;
        }

        const string banner =
            "      _ _           _                  \n" +
            "     | | |         | |                 \n" +
            "  __ | | | ___ __ _| |_ _ __ __ _ ____ \n" +
            " / _`| | |/ __/ _` | __| '__/ _` |_  / \n" +
            "| (_| | | | (_| (_| | |_| | | (_| |/ /  \n" +
            " \\__,_|_|_|\\___\\__,_|\\__|_|  \\__,_/___| ";

        AnsiConsole.MarkupLine($"[orange1]{Markup.Escape(banner)}[/]");
        AnsiConsole.MarkupLine("[grey]  serverless sandboxes, on demand.[/]");
        AnsiConsole.WriteLine();
    }

    private static IConfiguration BuildConfiguration(string[] args)
    {
        // Layered (least to most specific):
        // 1. compiled-in defaults
        // 2. ~/.config/alcatraz/config.json (flat top-level keys)
        // 3. environment variables (ALCATRAZ_CLI__APIBASEURL, ...)
        // 4. --api-url <url> command-line override
        var defaults = new Dictionary<string, string?>
        {
            ["Cli:ApiBaseUrl"] = CliConfig.Default.ApiBaseUrl,
            ["Cli:AlwaysUseGatewayProxy"] = CliConfig.Default.AlwaysUseGatewayProxy ? "true" : "false",
        };

        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(defaults);

        var fromFile = new CliConfigStore().Load();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cli:ApiBaseUrl"] = fromFile.ApiBaseUrl,
            ["Cli:AlwaysUseGatewayProxy"] = fromFile.AlwaysUseGatewayProxy ? "true" : "false",
        });

        builder.AddEnvironmentVariables(prefix: "ALCATRAZ_");

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--api-url")
            {
                builder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cli:ApiBaseUrl"] = args[i + 1],
                });
                break;
            }
        }

        return builder.Build();
    }
}

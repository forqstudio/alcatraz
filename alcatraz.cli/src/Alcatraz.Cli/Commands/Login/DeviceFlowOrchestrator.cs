using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Authentication;

namespace Alcatraz.Cli.Commands.Login;

public interface IDeviceFlowOrchestrator
{
    Task<StoredTokens> RunAsync(
        DeviceAuthorizationResponse init,
        CancellationToken ct);
}

internal sealed class DeviceFlowOrchestrator(
    IAlcatrazApiClient api,
    ITokenStore tokens,
    TimeProvider clock) : IDeviceFlowOrchestrator
{
    public async Task<StoredTokens> RunAsync(
        DeviceAuthorizationResponse init,
        CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddSeconds(init.ExpiresIn);
        var interval = TimeSpan.FromSeconds(Math.Max(1, init.Interval));

        while (clock.GetUtcNow() < deadline)
        {
            await Task.Delay(interval, clock, ct);

            var poll = await api.PollDeviceTokenAsync(init.DeviceCode, ct);
            switch (poll.ErrorKind)
            {
                case DeviceTokenError.None:
                    var token = poll.Token!;
                    var stored = new StoredTokens(
                        token.AccessToken,
                        token.RefreshToken,
                        clock.GetUtcNow().UtcDateTime.AddSeconds(token.ExpiresIn),
                        token.TokenType,
                        token.IdToken);
                    await tokens.SaveAsync(stored, ct);
                    return stored;

                case DeviceTokenError.AuthorizationPending:
                    continue;

                case DeviceTokenError.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case DeviceTokenError.ExpiredToken:
                    throw new ExpiredDeviceCodeException();

                case DeviceTokenError.AccessDenied:
                    throw new AuthorizationDeniedException();

                default:
                    throw new ApiUnavailableException(poll.ErrorDetail ?? "device-flow polling failed");
            }
        }

        throw new ExpiredDeviceCodeException();
    }
}

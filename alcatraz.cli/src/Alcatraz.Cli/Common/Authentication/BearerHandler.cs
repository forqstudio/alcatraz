using System.Net;
using System.Net.Http.Headers;
using Alcatraz.Cli.Common.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Alcatraz.Cli.Common.Authentication;

internal sealed class BearerHandler(
    ITokenStore tokens,
    IServiceProvider services,
    TimeProvider clock,
    ILogger<BearerHandler> log) : DelegatingHandler
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var anon = request.Options.TryGetValue(AlcatrazApiClient.AnonRequestOption, out var v) && v;

        if (!anon)
        {
            await AttachBearerAsync(request, cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (!anon && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            if (await TryRefreshAsync(forceNetworkCall: true, cancellationToken))
            {
                await AttachBearerAsync(request, cancellationToken, force: true);
                return await base.SendAsync(request, cancellationToken);
            }

            throw new NotLoggedInException();
        }

        return response;
    }

    private async Task AttachBearerAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        bool force = false)
    {
        var stored = await tokens.LoadAsync(ct)
            ?? throw new NotLoggedInException();

        var nearExpiry = stored.AccessTokenExpiresAtUtc - clock.GetUtcNow().UtcDateTime < RefreshSkew;
        if (force || nearExpiry)
        {
            if (await TryRefreshAsync(forceNetworkCall: force, ct))
            {
                stored = await tokens.LoadAsync(ct) ?? stored;
            }
        }

        request.Headers.Authorization = new AuthenticationHeaderValue(
            stored.TokenType,
            stored.AccessToken);
    }

    private async Task<bool> TryRefreshAsync(bool forceNetworkCall, CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            var stored = await tokens.LoadAsync(ct);
            if (stored?.RefreshToken is null)
            {
                return false;
            }

            // If a concurrent waiter already refreshed, skip the network call —
            // unless the caller is reacting to a 401, in which case the cached
            // token may have been revoked despite still appearing fresh.
            if (!forceNetworkCall &&
                stored.AccessTokenExpiresAtUtc - clock.GetUtcNow().UtcDateTime > RefreshSkew)
            {
                return true;
            }

            var api = services.GetRequiredService<IAlcatrazApiClient>();
            try
            {
                var fresh = await api.RefreshDeviceTokenAsync(stored.RefreshToken, ct);
                var refreshed = new StoredTokens(
                    fresh.AccessToken,
                    fresh.RefreshToken ?? stored.RefreshToken,
                    clock.GetUtcNow().UtcDateTime.AddSeconds(fresh.ExpiresIn),
                    fresh.TokenType,
                    fresh.IdToken ?? stored.IdToken);
                await tokens.SaveAsync(refreshed, ct);
                return true;
            }
            catch (NotLoggedInException)
            {
                tokens.Clear();
                return false;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Refresh token call failed");
                return false;
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Commands.Sandboxes;
using Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;
using Alcatraz.Cli.Commands.Sandboxes.Usage;

namespace Alcatraz.Cli.Common.Api;

internal sealed class AlcatrazApiClient(HttpClient http) : IAlcatrazApiClient
{
    public static readonly HttpRequestOptionsKey<bool> AnonRequestOption = new("anon");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeviceAuthorizationResponse> InitiateDeviceAuthAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/device");
        request.Options.Set(AnonRequestOption, true);
        using var response = await SendRawAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>(Json, ct))!;
    }

    public async Task<DeviceTokenExchangeResult> PollDeviceTokenAsync(string deviceCode, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/device/token")
        {
            Content = JsonContent.Create(new { deviceCode }, options: Json),
        };
        request.Options.Set(AnonRequestOption, true);

        using var response = await SendRawAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var token = (await response.Content.ReadFromJsonAsync<DeviceTokenResponse>(Json, ct))!;
            return new DeviceTokenExchangeResult(token, DeviceTokenError.None, null);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (kind, detail) = await ReadDeviceErrorAsync(response, ct);
            return new DeviceTokenExchangeResult(null, kind, detail);
        }

        await EnsureSuccessAsync(response, ct);
        return new DeviceTokenExchangeResult(null, DeviceTokenError.Other, "unreachable");
    }

    public async Task<DeviceTokenResponse> RefreshDeviceTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken }, options: Json),
        };
        request.Options.Set(AnonRequestOption, true);

        using var response = await SendRawAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<DeviceTokenResponse>(Json, ct))!;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var (_, _) = await ReadDeviceErrorAsync(response, ct);
            // invalid_grant or refresh_failed both mean the refresh token is dead
            throw new NotLoggedInException();
        }

        await EnsureSuccessAsync(response, ct);
        throw new ApiUnavailableException("unreachable");
    }

    public async Task<SandboxResponse> CreateSandboxAsync(int vcpus, int memoryMib, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/v1/sandboxes",
            new { vcpus, memoryMib },
            Json,
            ct);
        await EnsureSandboxSuccessAsync(response, sandboxId: null, ct);
        return (await response.Content.ReadFromJsonAsync<SandboxResponse>(Json, ct))!;
    }

    public async Task<IReadOnlyList<SandboxResponse>> ListSandboxesAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/sandboxes", ct);
        await EnsureSandboxSuccessAsync(response, sandboxId: null, ct);
        return (await response.Content.ReadFromJsonAsync<List<SandboxResponse>>(Json, ct))!;
    }

    public async Task<SandboxResponse> GetSandboxAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/sandboxes/{id}", ct);
        await EnsureSandboxSuccessAsync(response, id, ct);
        return (await response.Content.ReadFromJsonAsync<SandboxResponse>(Json, ct))!;
    }

    public async Task DeleteSandboxAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sandboxes/{id}", ct);
        await EnsureSandboxSuccessAsync(response, id, ct);
    }

    public async Task<SshCertificateResponse> IssueSshCertificateAsync(
        Guid sandboxId,
        string sshPublicKey,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/v1/sandboxes/{sandboxId}/ssh-cert",
            new { sshPublicKey },
            Json,
            ct);
        await EnsureSandboxSuccessAsync(response, sandboxId, ct);
        return (await response.Content.ReadFromJsonAsync<SshCertificateResponse>(Json, ct))!;
    }

    public async Task<SandboxUsageResponse> GetSandboxUsageAsync(Guid sandboxId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/v1/sandboxes/{sandboxId}/usage", ct);
        await EnsureSandboxSuccessAsync(response, sandboxId, ct);
        return (await response.Content.ReadFromJsonAsync<SandboxUsageResponse>(Json, ct))!;
    }

    public async Task<IReadOnlyList<SandboxUsageResponse>> ListSandboxUsageAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/v1/sandboxes/usage", ct);
        await EnsureSandboxSuccessAsync(response, sandboxId: null, ct);
        return (await response.Content.ReadFromJsonAsync<List<SandboxUsageResponse>>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiUnavailableException(ex.Message, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await SafeReadStringAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new BadRequestException(body),
            HttpStatusCode.Conflict => new ConflictException(body),
            >= HttpStatusCode.InternalServerError => new ApiUnavailableException(
                $"HTTP {(int)response.StatusCode}: {body}"),
            _ => new ApiUnavailableException($"HTTP {(int)response.StatusCode}: {body}"),
        };
    }

    private static async Task EnsureSandboxSuccessAsync(
        HttpResponseMessage response,
        Guid? sandboxId,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound && sandboxId is { } id)
        {
            throw new SandboxNotFoundException(id);
        }

        await EnsureSuccessAsync(response, ct);
    }

    private static async Task<(DeviceTokenError Kind, string? Detail)> ReadDeviceErrorAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (problem.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.String)
            {
                var error = errorProp.GetString();
                return MapDeviceError(error);
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return (DeviceTokenError.Other, null);
    }

    private static (DeviceTokenError Kind, string? Detail) MapDeviceError(string? error) =>
        error switch
        {
            "authorization_pending" => (DeviceTokenError.AuthorizationPending, error),
            "slow_down" => (DeviceTokenError.SlowDown, error),
            "expired_token" => (DeviceTokenError.ExpiredToken, error),
            "access_denied" => (DeviceTokenError.AccessDenied, error),
            _ => (DeviceTokenError.Other, error),
        };

    private static async Task<string> SafeReadStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }
}

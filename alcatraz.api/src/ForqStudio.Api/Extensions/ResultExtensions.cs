using System.Text;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Security;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;
using Microsoft.AspNetCore.Mvc;

namespace ForqStudio.Api.Extensions;

internal static class ResultExtensions
{
    public static IActionResult ToFailureActionResult(this Error error)
    {
        if (error == SandboxErrors.NotFound)
        {
            return new NotFoundResult();
        }

        if (IsDeviceAuthError(error))
        {
            var problem = new ProblemDetails
            {
                Title = "device_authorization_error",
                Detail = error.name,
                Status = StatusCodes.Status400BadRequest,
            };
            problem.Extensions["error"] = TrimPrefix(error.Code, "Auth.Device.");

            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }

        if (error == SshCertificateErrors.InvalidPublicKey)
        {
            return new BadRequestObjectResult(error);
        }

        if (error == SshCertificateErrors.SigningFailed)
        {
            return new ObjectResult(error) { StatusCode = StatusCodes.Status500InternalServerError };
        }

        if (error == SandboxErrors.AlreadyDeleting ||
            error == SandboxErrors.AlreadyDeleted ||
            error == SandboxErrors.InvalidStateForCertIssue)
        {
            return new ConflictObjectResult(error);
        }

        return new ObjectResult(error) { StatusCode = StatusCodes.Status500InternalServerError };
    }

    private static bool IsDeviceAuthError(Error error) =>
        error == DeviceAuthErrors.AuthorizationPending ||
        error == DeviceAuthErrors.SlowDown ||
        error == DeviceAuthErrors.ExpiredToken ||
        error == DeviceAuthErrors.AccessDenied ||
        error == DeviceAuthErrors.InitiationFailed ||
        error == DeviceAuthErrors.ExchangeFailed;

    private static string TrimPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal)
            ? char.ToLowerInvariant(value[prefix.Length]) + InsertUnderscores(value[(prefix.Length + 1)..])
            : value;

    private static string InsertUnderscores(string camelCase)
    {
        if (string.IsNullOrEmpty(camelCase))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(camelCase.Length + 4);
        foreach (var ch in camelCase)
        {
            if (char.IsUpper(ch))
            {
                builder.Append('_');
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }
}

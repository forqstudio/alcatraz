using ForqStudio.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace ForqStudio.Api.Middleware;

public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private const string ValidationFailureType = "ValidationFailure";
    private const string ValidationErrorTitle = "Validation error";
    private const string ValidationErrorDetail = "One or more validation errors has occurred";
    private const string ServerErrorType = "ServerError";
    private const string ServerErrorTitle = "Server error";
    private const string ServerErrorDetail = "An unexpected error has occurred";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Exception occurred: {Message}", exception.Message);

            var exceptionDetails = GetExceptionDetails(exception);

            var problemDetails = new ProblemDetails
            {
                Status = exceptionDetails.Status,
                Type = exceptionDetails.Type,
                Title = exceptionDetails.Title,
                Detail = exceptionDetails.Detail,
            };

            if (exceptionDetails.Errors is not null)
            {
                problemDetails.Extensions["errors"] = exceptionDetails.Errors;
            }

            context.Response.StatusCode = exceptionDetails.Status;

            await context.Response.WriteAsJsonAsync(problemDetails);
        }
    }

    private static ExceptionDetails GetExceptionDetails(Exception exception)
    {
        return exception switch
        {
            ValidationException validationException => new ExceptionDetails(
                StatusCodes.Status400BadRequest,
                ValidationFailureType,
                ValidationErrorTitle,
                ValidationErrorDetail,
                validationException.Errors),
            _ => new ExceptionDetails(
                StatusCodes.Status500InternalServerError,
                ServerErrorType,
                ServerErrorTitle,
                ServerErrorDetail,
                null)
        };
    }

    internal record ExceptionDetails(
        int Status,
        string Type,
        string Title,
        string Detail,
        IEnumerable<object>? Errors);
}
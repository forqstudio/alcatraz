using ForqStudio.Api.Middleware;
using ForqStudio.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForqStudio.Api.Extensions;

internal static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder ApplyMigrations(this IApplicationBuilder builder)
    {
        using var scope = builder.ApplicationServices.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        dbContext.Database.Migrate();

        return builder;
    }

    public static IApplicationBuilder UseCustomExceptionHandler(this IApplicationBuilder builder)
    {
        builder.UseMiddleware<ExceptionHandlingMiddleware>();

        return builder;
    }

    public static IApplicationBuilder UseRequestContextLogging(this IApplicationBuilder builder)
    {
        builder.UseMiddleware<RequestContextLoggingMiddleware>();

        return builder;
    }
}




using ForqStudio.Application.Abstractions.Behaviors;
using ForqStudio.Domain.Bookings;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ForqStudio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) 
    {
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly);
            configuration.AddOpenBehavior(typeof(LoggingBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
            configuration.AddOpenBehavior(typeof(QueryCachingBehavior<,>));
        });

        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true); 

        services.AddTransient<PricingService>();

        return services;
    }
}

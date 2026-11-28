using EmailService.Application.Security;
using EmailService.Application.UseCases.GetEmailStatus;
using EmailService.Application.UseCases.SendEmail;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmailService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Relógio injetável: os testes podem substituí-lo por um FakeTimeProvider.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton(new SendEmailOptions());
        services.AddScoped<SendEmailHandler>();
        services.AddScoped<GetEmailStatusHandler>();
        services.AddScoped<ApiKeyAuthenticator>();
        services.AddValidatorsFromAssemblyContaining<SendEmailCommandValidator>(ServiceLifetime.Singleton);

        return services;
    }
}

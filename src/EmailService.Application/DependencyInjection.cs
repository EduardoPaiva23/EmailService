using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmailService.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Relógio injetável: os testes podem substituí-lo por um FakeTimeProvider.
        services.TryAddSingleton(TimeProvider.System);

        // Casos de uso e validadores serão registrados aqui (Etapa 3).
        return services;
    }
}

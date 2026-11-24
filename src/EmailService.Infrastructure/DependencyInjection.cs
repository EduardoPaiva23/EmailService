using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // DbContext, repositórios, RabbitMQ e SMTP serão registrados aqui
        // (Etapas 2, 4 e 6).
        return services;
    }
}

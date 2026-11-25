using EmailService.Application.Abstractions.Persistence;
using EmailService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "EmailServiceDb";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
        {
            // Validado só quando o DbContext é usado, para que a composição
            // possa ser verificada sem banco configurado.
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' não configurada. " +
                    $"Defina ConnectionStrings:{ConnectionStringName} (User Secrets ou variável de ambiente).");

            options.UseSqlServer(connectionString);
        });

        services.AddScoped<IEmailRepository, EmailRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IIdempotencyKeyRepository, IdempotencyKeyRepository>();
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        var seedSection = configuration.GetSection(DevelopmentSeedOptions.SectionName);
        services.AddSingleton(new DevelopmentSeedOptions(seedSection["ApiKey"], seedSection["ClientName"]));
        services.AddScoped<DevelopmentDataSeeder>();

        // RabbitMQ e SMTP serão registrados aqui (Etapas 4 e 6).
        return services;
    }
}

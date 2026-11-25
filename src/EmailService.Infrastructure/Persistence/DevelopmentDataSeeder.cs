using EmailService.Application.Security;
using EmailService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmailService.Infrastructure.Persistence;

/// <summary>Seção de configuração <c>DevelopmentSeed</c>.</summary>
public sealed record DevelopmentSeedOptions(string? ApiKey, string? ClientName)
{
    public const string SectionName = "DevelopmentSeed";
}

/// <summary>
/// Cria um ApiClient de desenvolvimento a partir de <c>DevelopmentSeed:ApiKey</c>.
/// A chave vem da configuração local (User Secrets ou variável de ambiente)
/// e nunca do código, e só o hash é gravado. Deve ser chamado apenas em
/// Development.
/// </summary>
public sealed class DevelopmentDataSeeder(
    AppDbContext db,
    DevelopmentSeedOptions options,
    TimeProvider timeProvider,
    ILogger<DevelopmentDataSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = options.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation("DevelopmentSeed:ApiKey não configurada; seed de desenvolvimento ignorado.");
            return;
        }

        var prefix = ApiKeyHasher.GetPrefix(apiKey);
        if (await db.ApiClients.AnyAsync(c => c.KeyPrefix == prefix, cancellationToken))
            return;

        var name = string.IsNullOrWhiteSpace(options.ClientName) ? "Cliente de desenvolvimento" : options.ClientName;
        db.ApiClients.Add(ApiClient.Create(name, prefix, ApiKeyHasher.Hash(apiKey), timeProvider.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("ApiClient de desenvolvimento '{Name}' criado (prefixo {Prefix}).", name, prefix);
    }
}

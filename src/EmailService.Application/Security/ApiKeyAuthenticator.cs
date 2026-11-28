using System.Security.Cryptography;
using System.Text;
using EmailService.Application.Abstractions.Persistence;

namespace EmailService.Application.Security;

public sealed class ApiKeyAuthenticator(IApiClientRepository clients)
{
    /// <returns>O ClientId, ou null se a chave for ausente, inválida ou de um cliente inativo.</returns>
    public async Task<Guid?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < ApiKeyHasher.MinimumKeyLength)
            return null;

        var client = await clients.GetByKeyPrefixAsync(ApiKeyHasher.GetPrefix(apiKey), cancellationToken);
        if (client is null || !client.IsActive)
            return null;

        // Comparação em tempo constante, para não vazar o hash por timing.
        var expected = Encoding.ASCII.GetBytes(client.KeyHash);
        var actual = Encoding.ASCII.GetBytes(ApiKeyHasher.Hash(apiKey));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? client.Id : null;
    }
}

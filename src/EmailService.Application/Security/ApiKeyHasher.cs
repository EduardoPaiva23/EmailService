using System.Security.Cryptography;
using System.Text;

namespace EmailService.Application.Security;

/// <summary>
/// Hash e prefixo de API keys. A chave em texto puro nunca é armazenada:
/// o prefixo localiza o cliente e o hash SHA-256 confirma a chave.
/// </summary>
public static class ApiKeyHasher
{
    public const int PrefixLength = 12;
    public const int MinimumKeyLength = 32;

    public static string Hash(string apiKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    }

    public static string GetPrefix(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        if (apiKey.Length < MinimumKeyLength)
            throw new ArgumentException(
                $"A API key deve ter pelo menos {MinimumKeyLength} caracteres.", nameof(apiKey));

        return apiKey[..PrefixLength];
    }
}

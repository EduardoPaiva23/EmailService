using EmailService.Domain.Exceptions;

namespace EmailService.Domain.Entities;

/// <summary>Cliente autorizado. Apenas o hash da API key é armazenado.</summary>
public sealed class ApiClient
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>Primeiros caracteres da chave, para localizar o cliente e exibir.</summary>
    public string KeyPrefix { get; private set; } = null!;

    /// <summary>SHA-256 da chave, em hexadecimal.</summary>
    public string KeyHash { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private ApiClient() { } // EF Core

    public static ApiClient Create(string name, string keyPrefix, string keyHash, DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("'name' é obrigatório.");
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new DomainException("'keyPrefix' é obrigatório.");
        if (string.IsNullOrWhiteSpace(keyHash))
            throw new DomainException("'keyHash' é obrigatório.");

        return new ApiClient
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            IsActive = true,
            CreatedAt = createdAt
        };
    }

    public void Deactivate() => IsActive = false;
}

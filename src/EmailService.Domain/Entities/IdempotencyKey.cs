using EmailService.Domain.Exceptions;

namespace EmailService.Domain.Entities;

/// <summary>Chave de idempotência de uma solicitação (PK: ClientId + Key).</summary>
public sealed class IdempotencyKey
{
    public Guid ClientId { get; private set; }
    public string Key { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public Guid EmailId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    private IdempotencyKey() { } // EF Core

    public static IdempotencyKey Create(
        Guid clientId, string key, string requestHash, Guid emailId, DateTime createdAt, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("'key' é obrigatório.");
        if (string.IsNullOrWhiteSpace(requestHash))
            throw new DomainException("'requestHash' é obrigatório.");
        if (ttl <= TimeSpan.Zero)
            throw new DomainException("O TTL deve ser positivo.");

        return new IdempotencyKey
        {
            ClientId = clientId,
            Key = key,
            RequestHash = requestHash,
            EmailId = emailId,
            CreatedAt = createdAt,
            ExpiresAt = createdAt + ttl
        };
    }

    /// <summary>True se a solicitação repetida tem o mesmo payload da original.</summary>
    public bool Matches(string requestHash) => RequestHash == requestHash;
}

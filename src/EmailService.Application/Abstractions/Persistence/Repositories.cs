using EmailService.Domain.Entities;

namespace EmailService.Application.Abstractions.Persistence;

// Os repositórios só preparam alterações; nada é gravado até
// IUnitOfWork.SaveChangesAsync, que persiste tudo em uma única transação.

public interface IEmailRepository
{
    void Add(Email email);
    void AddAttempt(EmailAttempt attempt);
    Task<Email?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IOutboxRepository
{
    void Add(OutboxMessage message);
}

public interface IIdempotencyKeyRepository
{
    void Add(IdempotencyKey key);

    /// <summary>Marca uma chave lida por <see cref="GetAsync"/> como alterada.</summary>
    void Update(IdempotencyKey key);

    /// <summary>Lê a chave direto do banco, sem rastreamento.</summary>
    Task<IdempotencyKey?> GetAsync(Guid clientId, string key, CancellationToken cancellationToken = default);
}

public interface IApiClientRepository
{
    void Add(ApiClient client);
    Task<ApiClient?> GetByKeyPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);
}

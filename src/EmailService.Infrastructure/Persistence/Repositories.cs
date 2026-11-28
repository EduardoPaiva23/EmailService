using EmailService.Application.Abstractions.Persistence;
using EmailService.Application.Exceptions;
using EmailService.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EmailService.Infrastructure.Persistence;

internal sealed class EmailRepository(AppDbContext db) : IEmailRepository
{
    public void Add(Email email) => db.Emails.Add(email);

    public void AddAttempt(EmailAttempt attempt) => db.EmailAttempts.Add(attempt);

    public Task<Email?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Emails.SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
}

internal sealed class OutboxRepository(AppDbContext db) : IOutboxRepository
{
    public void Add(OutboxMessage message) => db.OutboxMessages.Add(message);
}

internal sealed class IdempotencyKeyRepository(AppDbContext db) : IIdempotencyKeyRepository
{
    public void Add(IdempotencyKey key) => db.IdempotencyKeys.Add(key);

    public void Update(IdempotencyKey key) => db.IdempotencyKeys.Update(key);

    // Sem rastreamento: depois de uma DuplicateKeyException, o contexto ainda
    // rastreia a chave que falhou ao gravar, e uma consulta rastreada
    // devolveria essa instância em vez da linha vencedora do banco.
    public Task<IdempotencyKey?> GetAsync(Guid clientId, string key, CancellationToken cancellationToken = default) =>
        db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.ClientId == clientId && k.Key == key, cancellationToken);
}

internal sealed class ApiClientRepository(AppDbContext db) : IApiClientRepository
{
    public void Add(ApiClient client) => db.ApiClients.Add(client);

    public Task<ApiClient?> GetByKeyPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
        db.ApiClients.SingleOrDefaultAsync(c => c.KeyPrefix == keyPrefix, cancellationToken);
}

/// <summary>
/// O SaveChanges do EF Core grava todas as alterações rastreadas em uma
/// única transação: ou tudo é persistido, ou nada é.
/// </summary>
internal sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyConflictException(
                "O registro foi alterado por outro processo depois de ser lido.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // 2627: violação de PK/UNIQUE constraint; 2601: violação de índice único.
            throw new DuplicateKeyException("A gravação violou uma chave única.", ex);
        }
    }
}

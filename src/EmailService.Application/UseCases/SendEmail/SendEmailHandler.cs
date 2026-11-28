using EmailService.Application.Abstractions.Persistence;
using EmailService.Application.Exceptions;
using EmailService.Application.Messaging;
using EmailService.Domain.Entities;

namespace EmailService.Application.UseCases.SendEmail;

/// <summary>
/// Registra a solicitação de envio: o e-mail (Pending), o evento na Outbox e
/// a Idempotency-Key são gravados na mesma transação.
/// </summary>
public sealed class SendEmailHandler(
    IEmailRepository emails,
    IOutboxRepository outbox,
    IIdempotencyKeyRepository idempotencyKeys,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    SendEmailOptions options)
{
    public async Task<SendEmailResult> HandleAsync(SendEmailCommand command, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var requestHash = SendEmailRequestHash.Compute(command);

        var existingKey = await idempotencyKeys.GetAsync(command.ClientId, command.IdempotencyKey, cancellationToken);
        if (existingKey is not null && !existingKey.IsExpired(now))
            return await ReplayAsync(existingKey, requestHash, command.IdempotencyKey, cancellationToken);

        var email = Email.Create(
            command.ClientId, command.CorrelationId, command.To, command.Subject, command.Body,
            command.IsHtml, options.MaxAttempts, now);
        emails.Add(email);

        var message = new SendEmailMessage(email.Id, email.CorrelationId, email.CreatedAt);
        outbox.Add(OutboxMessage.Create(SendEmailMessage.Type, message.ToJson(), occurredAt: now, availableAt: now));

        if (existingKey is null)
        {
            idempotencyKeys.Add(IdempotencyKey.Create(
                command.ClientId, command.IdempotencyKey, requestHash, email.Id, now, options.IdempotencyKeyTtl));
        }
        else
        {
            // Chave expirada que a limpeza ainda não removeu.
            existingKey.Renew(requestHash, email.Id, now, options.IdempotencyKeyTtl);
            idempotencyKeys.Update(existingKey);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            // Outra requisição com a mesma chave gravou primeiro: responde como repetição.
            var winner = await idempotencyKeys.GetAsync(command.ClientId, command.IdempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException("Chave duplicada, mas a chave vencedora não foi encontrada.");
            return await ReplayAsync(winner, requestHash, command.IdempotencyKey, cancellationToken);
        }

        return new SendEmailResult(email.Id, email.Status, email.CreatedAt, Replayed: false);
    }

    private async Task<SendEmailResult> ReplayAsync(
        IdempotencyKey key, string requestHash, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!key.Matches(requestHash))
            throw new IdempotencyConflictException(idempotencyKey);

        var original = await emails.GetByIdAsync(key.EmailId, cancellationToken)
            ?? throw new InvalidOperationException($"E-mail {key.EmailId} da Idempotency-Key não encontrado.");

        return new SendEmailResult(original.Id, original.Status, original.CreatedAt, Replayed: true);
    }
}

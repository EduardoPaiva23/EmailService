using EmailService.Domain.Exceptions;

namespace EmailService.Domain.Entities;

/// <summary>
/// Evento aguardando publicação no RabbitMQ (Transactional Outbox).
/// Lease e publicação são controlados pelo Outbox Dispatcher (Etapa 4).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;
    public string Payload { get; private set; } = null!;
    public DateTime OccurredAt { get; private set; }
    public DateTime AvailableAt { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public int PublishAttempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LockedUntil { get; private set; }
    public string? LockedBy { get; private set; }

    private OutboxMessage() { } // EF Core

    public static OutboxMessage Create(string type, string payload, DateTime occurredAt, DateTime availableAt)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new DomainException("'type' é obrigatório.");
        if (string.IsNullOrWhiteSpace(payload))
            throw new DomainException("'payload' é obrigatório.");
        if (availableAt < occurredAt)
            throw new DomainException("AvailableAt não pode ser anterior a OccurredAt.");

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
            AvailableAt = availableAt
        };
    }
}

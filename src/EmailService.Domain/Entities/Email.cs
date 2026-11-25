using EmailService.Domain.Enums;
using EmailService.Domain.Exceptions;

namespace EmailService.Domain.Entities;

/// <summary>
/// E-mail solicitado por um cliente. As transições de status são feitas
/// apenas pelos métodos desta classe; transições inválidas lançam
/// <see cref="InvalidEmailStatusTransitionException"/>.
/// </summary>
public sealed class Email
{
    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string Recipient { get; private set; } = null!;
    public string Subject { get; private set; } = null!;
    public string Body { get; private set; } = null!;
    public bool IsHtml { get; private set; }
    public EmailStatus Status { get; private set; }

    /// <summary>Tentativas de envio já concluídas.</summary>
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }

    /// <summary>Informativo; o agendamento real é o AvailableAt da Outbox.</summary>
    public DateTime? NextAttemptAt { get; private set; }

    /// <summary>Lease do processamento, usado para recuperar Processing abandonado.</summary>
    public DateTime? LockedUntil { get; private set; }
    public string? LastError { get; private set; }
    public string? ProviderMessageId { get; private set; }

    /// <summary>Concorrência otimista (rowversion no SQL Server).</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>True se, ao concluir a tentativa atual, ainda restar outra.</summary>
    public bool HasAttemptsRemaining => Attempts + 1 < MaxAttempts;

    private Email() { } // EF Core

    public static Email Create(
        Guid clientId,
        Guid correlationId,
        string recipient,
        string subject,
        string body,
        bool isHtml,
        int maxAttempts,
        DateTime now)
    {
        RequireText(recipient, nameof(recipient));
        RequireText(subject, nameof(subject));
        RequireText(body, nameof(body));
        if (maxAttempts < 1)
            throw new DomainException("MaxAttempts deve ser pelo menos 1.");

        return new Email
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            CorrelationId = correlationId,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            IsHtml = isHtml,
            Status = EmailStatus.Pending,
            MaxAttempts = maxAttempts,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Pending -> Processing, reservando o e-mail até <paramref name="lockedUntil"/>.</summary>
    public void StartProcessing(DateTime now, DateTime lockedUntil)
    {
        EnsureStatus(EmailStatus.Pending, nameof(StartProcessing));
        if (lockedUntil <= now)
            throw new DomainException("LockedUntil deve estar no futuro.");

        Status = EmailStatus.Processing;
        LockedUntil = lockedUntil;
        UpdatedAt = now;
    }

    /// <summary>Processing -> Sent: o provedor aceitou a mensagem.</summary>
    public void MarkSent(DateTime now, string? providerMessageId)
    {
        EnsureStatus(EmailStatus.Processing, nameof(MarkSent));

        CompleteAttempt(now);
        Status = EmailStatus.Sent;
        SentAt = now;
        ProviderMessageId = providerMessageId;
        NextAttemptAt = null;
    }

    /// <summary>Processing -> Pending: erro temporário/ambíguo ou lease expirado.</summary>
    public void ScheduleRetry(DateTime now, DateTime nextAttemptAt, string error)
    {
        EnsureStatus(EmailStatus.Processing, nameof(ScheduleRetry));
        if (!HasAttemptsRemaining)
            throw new DomainException("Não restam tentativas; use MarkFailed.");
        if (nextAttemptAt < now)
            throw new DomainException("NextAttemptAt não pode estar no passado.");

        CompleteAttempt(now);
        Status = EmailStatus.Pending;
        NextAttemptAt = nextAttemptAt;
        LastError = error;
    }

    /// <summary>Processing -> Failed: erro permanente ou tentativas esgotadas.</summary>
    public void MarkFailed(DateTime now, string error)
    {
        EnsureStatus(EmailStatus.Processing, nameof(MarkFailed));

        CompleteAttempt(now);
        Status = EmailStatus.Failed;
        LastError = error;
        NextAttemptAt = null;
    }

    /// <summary>Pending -> Cancelled (cancelamento manual).</summary>
    public void Cancel(DateTime now)
    {
        EnsureStatus(EmailStatus.Pending, nameof(Cancel));

        Status = EmailStatus.Cancelled;
        NextAttemptAt = null;
        UpdatedAt = now;
    }

    /// <summary>Failed -> Pending (retry manual), com o contador de tentativas zerado.</summary>
    public void RetryManually(DateTime now)
    {
        EnsureStatus(EmailStatus.Failed, nameof(RetryManually));

        Status = EmailStatus.Pending;
        Attempts = 0;
        NextAttemptAt = null;
        UpdatedAt = now;
    }

    private void CompleteAttempt(DateTime now)
    {
        Attempts++;
        LockedUntil = null;
        UpdatedAt = now;
    }

    private void EnsureStatus(EmailStatus expected, string operation)
    {
        if (Status != expected)
            throw new InvalidEmailStatusTransitionException(Status, operation);
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"'{name}' é obrigatório.");
    }
}
